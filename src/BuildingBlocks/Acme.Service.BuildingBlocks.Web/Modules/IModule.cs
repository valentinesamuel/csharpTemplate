using Acme.Service.BuildingBlocks.Security;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Service.BuildingBlocks.Web.Modules;

/// <summary>
/// The single public entry point of a module (plan §0.6, INV-02). The Host registers modules with
/// <see cref="ModuleExtensions.AddModules"/> and maps them with <see cref="ModuleExtensions.MapModuleEndpoints"/>.
/// </summary>
public interface IModule
{
    /// <summary>
    /// Module name. Equals the module's database schema and the key of its keyed unit of work and outbox (D-04).
    /// Lower-case ASCII letters, digits and underscores, starting with a letter (for example <c>orders</c>).
    /// </summary>
    string Name { get; }

    /// <summary>Permissions declared by the module, with their seed roles (plan §0.8).</summary>
    IReadOnlyList<PermissionDefinition> Permissions { get; }

    /// <summary>Registers the module's application services (use cases are registered by <see cref="ModuleExtensions.AddModules"/>).</summary>
    void AddServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment);

    /// <summary>Registers the module's persistence. Used by both the web app and the <c>migrate</c> command (§0.9).</summary>
    void AddPersistence(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Maps module-level routes. Every <see cref="Endpoints.IEndpoint"/> in the module assembly is already
    /// discovered and mapped by <see cref="ModuleExtensions.MapModuleEndpoints"/>; implement this only for
    /// anything extra, and do not map those endpoints again.
    /// </summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
