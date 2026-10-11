using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Service.BuildingBlocks.Web.Modules;

/// <summary>Host-side registration and mapping of modules.</summary>
public static class ModuleExtensions
{
    /// <summary>
    /// Registers <paramref name="modules"/> in order. For each module: its use cases and validators are
    /// registered under the module name (D-04), then <see cref="IModule.AddPersistence"/> and
    /// <see cref="IModule.AddServices"/> run. A <see cref="ModuleRegistry"/> singleton is added. Call once.
    /// </summary>
    public static TBuilder AddModules<TBuilder>(this TBuilder builder, params IModule[] modules)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(modules);

        var services = builder.Services;
        var registry = services.AddModuleRegistryCore(modules);

        foreach (var module in registry.Modules)
        {
            services.AddUseCasesFromAssembly(registry.GetAssembly(module.Name), module.Name);
            module.AddPersistence(services, builder.Configuration);
            module.AddServices(services, builder.Configuration, builder.Environment);
        }

        return builder;
    }

    /// <summary>
    /// Registers only the <see cref="ModuleRegistry"/> singleton for <paramref name="modules"/>, without any module
    /// services. For the minimal command hosts (<c>migrate</c>, <c>auth:grant-role</c>), which call
    /// <see cref="IModule.AddPersistence"/> themselves but whose post-migration steps need the module list (the Auth
    /// seeder reads every module's permissions). Call once; do not combine with <see cref="AddModules"/>.
    /// </summary>
    public static IServiceCollection AddModuleRegistry(this IServiceCollection services, params IModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(modules);

        services.AddModuleRegistryCore(modules);
        return services;
    }

    private static ModuleRegistry AddModuleRegistryCore(this IServiceCollection services, IModule[] modules)
    {
        if (services.Any(d => d.ServiceType == typeof(ModuleRegistry)))
        {
            throw new InvalidOperationException("Modules are already registered; register all modules in a single call.");
        }

        var registry = new ModuleRegistry([.. modules]);
        services.AddSingleton(registry);
        return registry;
    }

    /// <summary>
    /// For every registered module, in registration order, maps each <see cref="IEndpoint"/> found in the
    /// module assembly, then calls <see cref="IModule.MapEndpoints"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapModuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var registry = endpoints.ServiceProvider.GetService<ModuleRegistry>()
            ?? throw new InvalidOperationException("No modules are registered. Call AddModules before MapModuleEndpoints.");

        foreach (var module in registry.Modules)
        {
            EndpointDiscovery.MapEndpoints(endpoints, registry.GetAssembly(module.Name));
            module.MapEndpoints(endpoints);
        }

        return endpoints;
    }
}
