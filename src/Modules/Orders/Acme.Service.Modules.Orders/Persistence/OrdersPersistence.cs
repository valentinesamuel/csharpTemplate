using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.Service.Modules.Orders.Persistence;

/// <summary>Registers the Orders persistence adapter; the only place that wires EF types to the module's ports (D-01).</summary>
internal static class OrdersPersistence
{
    /// <summary>
    /// The <see cref="OrdersDbContext"/> with the keyed unit of work and outbox of module <paramref name="moduleKey"/>
    /// (D-04), <see cref="IOrderRepository"/>, and the generic repositories of <see cref="OrderAttachment"/> and
    /// <see cref="CustomerSnapshot"/>.
    /// </summary>
    public static IServiceCollection AddOrdersPersistence(this IServiceCollection services, IConfiguration configuration, string moduleKey)
    {
        services.AddModuleDbContext<OrdersDbContext>(configuration, moduleKey)
            .AddRepository<OrderAttachment, Guid>()
            .AddRepository<CustomerSnapshot, Guid>();
        services.TryAddScoped<IOrderRepository, OrderRepository>();
        return services;
    }
}
