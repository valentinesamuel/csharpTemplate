using Acme.Service.BuildingBlocks.Persistence.Messaging;
using Acme.Service.BuildingBlocks.Security;
using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Modules.Auth.Contracts.IntegrationEvents;
using Acme.Service.Modules.Orders.Contracts;
using Acme.Service.Modules.Orders.Contracts.IntegrationEvents;
using Acme.Service.Modules.Orders.IntegrationEventHandlers;
using Acme.Service.Modules.Orders.Jobs;
using Acme.Service.Modules.Orders.Options;
using Acme.Service.Modules.Orders.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Acme.Service.Modules.Orders;

/// <summary>
/// The Orders sample module (schema <c>orders</c>): the standard non-Auth module pattern. Use cases depend only on the
/// repository and unit-of-work ports, EF Core is confined to <c>Persistence/</c> (D-01, INV-04), and the endpoints under
/// <c>/api/v1/orders</c> combine permissions (§0.8) with ownership. It also demonstrates the cross-cutting patterns
/// (P17): an outbox event (<see cref="OrderPlacedV1"/>), inbox consumers of its own and of Auth's events, a job calling
/// another module through its Contracts, a lock-guarded recurring job, blob attachments and an idempotent create. The
/// only public type of the assembly (INV-02).
/// </summary>
public sealed class OrdersModule : IModule
{
    /// <inheritdoc />
    public string Name => OrdersModuleKey.Value;

    /// <inheritdoc />
    public IReadOnlyList<PermissionDefinition> Permissions => OrdersPermissions.Definitions;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Use cases and validators are registered by AddModules; the endpoints are discovered as IEndpoint types.
        services.TryAddSingleton(TimeProvider.System);
        OrdersOptions.Register(services, configuration);

        // Events produced (Orders.Contracts) and consumed (Auth.Contracts), for the persisted-name registry (INV-19).
        services.AddIntegrationEvents(typeof(OrderPlacedV1).Assembly, typeof(UserRegisteredV1).Assembly);

        // outbox -> OrderPlacedHandler (inbox) -> SendOrderPlacedNotificationJob -> IUserDirectory + INotificationSender.
        services.AddIntegrationEventHandler<OrderPlacedV1, OrderPlacedHandler>(Name, OrderPlacedHandler.ConsumerName);
        services.TryAddScoped<SendOrderPlacedNotificationJob>();

        // Auth's outbox -> UserRegisteredHandler (inbox) -> orders.customer_snapshots (INV-06 local read model).
        services.AddIntegrationEventHandler<UserRegisteredV1, UserRegisteredHandler>(Name, UserRegisteredHandler.ConsumerName);

        // The lock-guarded recurring expiry job, registered with the selected job queue at startup.
        services.TryAddScoped<ExpireStalePendingOrdersJob>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OrdersJobScheduler>());
    }

    /// <inheritdoc />
    public void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOrdersPersistence(configuration, Name);
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No module-level routes; feature endpoints are discovered as IEndpoint implementations.
    }
}
