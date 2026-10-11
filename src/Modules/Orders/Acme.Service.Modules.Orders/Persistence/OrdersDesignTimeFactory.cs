using Acme.Service.BuildingBlocks.Persistence.DesignTime;
using Microsoft.EntityFrameworkCore;

namespace Acme.Service.Modules.Orders.Persistence;

/// <summary>Design-time factory for <c>dotnet ef</c>. Internal: EF tools discover non-public factories (WH-09).</summary>
internal sealed class OrdersDesignTimeFactory : ModuleDesignTimeFactoryBase<OrdersDbContext>
{
    protected override string ModuleKey => OrdersDbContext.SchemaName;

    protected override OrdersDbContext CreateContext(DbContextOptions<OrdersDbContext> options) => new(options);
}
