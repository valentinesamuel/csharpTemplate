using Acme.Service.Modules.Orders.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.Modules.Orders.Persistence.Configurations;

/// <summary>
/// <c>orders.orders(id, customer_id, status, currency, total numeric(18,2), created_at, updated_at, deleted_at)</c> with
/// the <c>xmin</c> row version, and <c>orders.order_lines(id, order_id, line_number, sku, quantity, unit_price
/// numeric(18,2))</c>. Soft-deleted orders are excluded by a query filter. The list query is served by
/// <c>(customer_id, created_at desc)</c> and <c>(status)</c>.
/// </summary>
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    /// <summary>Longest status name stored.</summary>
    public const int StatusMaxLength = 16;

    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.CustomerId).IsRequired();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(StatusMaxLength).IsRequired();
        builder.Property(o => o.Currency).HasMaxLength(Order.CurrencyLength).IsFixedLength().IsRequired();
        builder.Property(o => o.Total).HasPrecision(18, 2);
        builder.Property(o => o.Version).IsRowVersion();

        builder.HasQueryFilter(o => o.DeletedAt == null);

        builder.HasIndex(o => new { o.CustomerId, o.CreatedAt }).IsDescending(false, true);
        builder.HasIndex(o => o.Status);

        builder.OwnsMany(o => o.Lines, lines =>
        {
            lines.ToTable("order_lines");
            lines.WithOwner().HasForeignKey("OrderId");
            lines.HasKey(l => l.Id);
            lines.Property(l => l.Id).ValueGeneratedNever();
            lines.Property(l => l.Sku).HasMaxLength(OrderLine.SkuMaxLength).IsRequired();
            lines.Property(l => l.UnitPrice).HasPrecision(18, 2);
            lines.Ignore(l => l.LineTotal);
        });
        builder.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
