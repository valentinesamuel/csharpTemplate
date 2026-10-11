using System.Reflection;
using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Persistence.Inbox;
using Acme.Service.BuildingBlocks.Persistence.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.Service.BuildingBlocks.Persistence.Messaging;

/// <summary>Registration of integration event types and consumers.</summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Adds the integration events declared in <paramref name="contractsAssemblies"/> to the type registry
    /// (INV-19). Call from every module that produces or consumes them; adding an assembly twice is harmless.
    /// Duplicate event names fail when the registry is built, which happens at startup (the outbox dispatchers
    /// resolve it when the host starts).
    /// </summary>
    public static IServiceCollection AddIntegrationEvents(this IServiceCollection services, params Assembly[] contractsAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contractsAssemblies);

        foreach (var assembly in contractsAssemblies)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            services.AddSingleton(new IntegrationEventAssembly(assembly));
        }

        services.AddMessagingCore();
        return services;
    }

    /// <summary>
    /// Adds explicit integration event types to the type registry (INV-19), for events that a module produces and
    /// consumes itself and therefore keeps <c>internal</c> (INV-02), which the Contracts assembly scan of
    /// <see cref="AddIntegrationEvents"/> does not see. Each type needs an <see cref="IntegrationEventAttribute"/> name
    /// that is unique across all registered events; adding a type twice is harmless.
    /// </summary>
    public static IServiceCollection AddIntegrationEventTypes(this IServiceCollection services, params Type[] eventTypes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(eventTypes);

        foreach (var eventType in eventTypes)
        {
            ArgumentNullException.ThrowIfNull(eventType);
            services.AddSingleton(new IntegrationEventType(eventType));
        }

        services.AddMessagingCore();
        return services;
    }

    /// <summary>
    /// Subscribes <typeparamref name="THandler"/> to <typeparamref name="TEvent"/> for the consuming module
    /// <paramref name="moduleKey"/>. This is the only supported way to register a consumer: the handler always
    /// runs through the inbox decorator, inside the consumer module's unit of work (INV-09). A handler type
    /// registered directly as <see cref="IIntegrationEventHandler{TEvent}"/> is never invoked.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="moduleKey">The consuming module; it must also call <c>AddModuleDbContext</c>.</param>
    /// <param name="consumerName">
    /// The stable name stored in <c>inbox_messages.consumer</c>. Defaults to <c>&lt;moduleKey&gt;.&lt;handler class name&gt;</c>;
    /// pass an explicit value to keep it stable across a handler rename (a changed name re-applies redelivered messages).
    /// </param>
    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler>(
        this IServiceCollection services,
        string moduleKey,
        string? consumerName = null)
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);

        var consumer = consumerName ?? $"{moduleKey}.{typeof(THandler).Name}";
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer, nameof(consumerName));

        if (services.Any(d => d.ImplementationInstance is IntegrationEventSubscription existing
                && existing.EventType == typeof(TEvent)
                && string.Equals(existing.ConsumerName, consumer, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Consumer '{consumer}' is already subscribed to {typeof(TEvent).Name}; consumer names must be unique per event.");
        }

        services.TryAddScoped<THandler>();
        services.AddSingleton(new IntegrationEventSubscription(
            typeof(TEvent),
            moduleKey,
            consumer,
            (scopedServices, integrationEvent, cancellationToken) =>
                InboxHandlerDecorator<TEvent>.Create<THandler>(scopedServices, moduleKey, consumer)
                    .HandleAsync((TEvent)integrationEvent, cancellationToken)));

        services.AddMessagingCore();
        return services;
    }

    /// <summary>Process-wide messaging services shared by every module. Idempotent.</summary>
    internal static IServiceCollection AddMessagingCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => IntegrationEventTypeRegistry.FromTypes(
            IntegrationEventTypeRegistry.ExportedEventTypes(sp.GetServices<IntegrationEventAssembly>().Select(a => a.Assembly))
                .Concat(sp.GetServices<IntegrationEventType>().Select(t => t.Type))));
        services.TryAddSingleton<IntegrationEventSerializer>();
        services.TryAddSingleton<IEventBus, InProcessEventBus>();
        services.TryAddSingleton(sp => new OutboxMetrics(sp.GetService<System.Diagnostics.Metrics.IMeterFactory>()));
        return services;
    }
}
