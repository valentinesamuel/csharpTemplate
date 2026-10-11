using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.Modules.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Acme.Service.Modules.Orders.Persistence;

/// <summary>
/// The Orders module context, schema <c>orders</c> (INV-06): <c>orders</c>, <c>order_lines</c>,
/// <c>order_attachments</c> and <c>customer_snapshots</c>, plus the module's outbox and inbox tables (from
/// <see cref="ModuleDbContext"/>). EF Core is used only in this namespace (D-01, INV-04).
/// </summary>
internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : ModuleDbContext(options)
{
    /// <summary>The module schema, equal to the module name.</summary>
    public const string SchemaName = OrdersModuleKey.Value;

    public override string Schema => SchemaName;

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderAttachment> OrderAttachments => Set<OrderAttachment>();

    public DbSet<CustomerSnapshot> CustomerSnapshots => Set<CustomerSnapshot>();

    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrdersDbContext).Assembly);
}
