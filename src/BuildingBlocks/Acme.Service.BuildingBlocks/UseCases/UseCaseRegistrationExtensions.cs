using System.Reflection;
using Acme.Service.BuildingBlocks.UseCases.Behaviors;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.Service.BuildingBlocks.UseCases;

/// <summary>Registers a module's use cases, validators and the shared behaviour pipeline.</summary>
public static class UseCaseRegistrationExtensions
{
    /// <summary>
    /// Scans <paramref name="assembly"/> and registers, as scoped services:
    /// every closed <see cref="IUseCase{TIn, TOut}"/> implementation (its input type is mapped to
    /// <paramref name="moduleKey"/> for unit-of-work selection), every <see cref="IValidator{T}"/>
    /// implementation, the open-generic <see cref="IUseCaseExecutor{TIn, TOut}"/>, and the behaviours in
    /// the fixed order Logging, Validation, Transaction (D-03). Safe to call once per module; shared
    /// registrations are added only once.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="assembly">Module assembly to scan.</param>
    /// <param name="moduleKey">Module name (equals its schema and its keyed unit-of-work key; D-04).</param>
    public static IServiceCollection AddUseCasesFromAssembly(this IServiceCollection services, Assembly assembly, string moduleKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);

        var registry = GetOrAddRegistry(services);

        services.TryAddScoped(typeof(IUseCaseExecutor<,>), typeof(UseCasePipeline<,>));

        // Registration order is execution order: first registered is outermost.
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IUseCaseBehavior<,>), typeof(LoggingBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IUseCaseBehavior<,>), typeof(ValidationBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IUseCaseBehavior<,>), typeof(TransactionBehavior<,>)));

        foreach (var type in assembly.DefinedTypes)
        {
            if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
            {
                continue;
            }

            foreach (var serviceType in type.ImplementedInterfaces)
            {
                if (!serviceType.IsGenericType)
                {
                    continue;
                }

                var definition = serviceType.GetGenericTypeDefinition();
                if (definition == typeof(IUseCase<,>))
                {
                    AddUseCase(services, serviceType, type);
                    registry.Register(serviceType.GenericTypeArguments[0], moduleKey);
                }
                else if (definition == typeof(IValidator<>))
                {
                    services.TryAddEnumerable(ServiceDescriptor.Scoped(serviceType, type));
                }
            }
        }

        return services;
    }

    private static void AddUseCase(IServiceCollection services, Type serviceType, Type implementationType)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == serviceType && !d.IsKeyedService);
        if (existing is null)
        {
            services.AddScoped(serviceType, implementationType);
            return;
        }

        if (existing.ImplementationType != implementationType)
        {
            throw new InvalidOperationException(
                $"'{serviceType}' is implemented by both '{existing.ImplementationType}' and '{implementationType}'.");
        }
    }

    private static UseCaseModuleRegistry GetOrAddRegistry(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(UseCaseModuleRegistry) && !d.IsKeyedService);
        if (descriptor?.ImplementationInstance is UseCaseModuleRegistry existing)
        {
            return existing;
        }

        var registry = new UseCaseModuleRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}
