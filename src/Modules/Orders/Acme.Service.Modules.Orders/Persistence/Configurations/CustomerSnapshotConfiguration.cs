using Acme.Service.Modules.Orders.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.Modules.Orders.Persistence.Configurations;

/// <summary><c>orders.customer_snapshots(id, email, display_name, updated_at)</c>: the local customer read model (INV-06).</summary>
internal sealed class CustomerSnapshotConfiguration : IEntityTypeConfiguration<CustomerSnapshot>
{
    public void Configure(EntityTypeBuilder<CustomerSnapshot> builder)
    {
        builder.ToTable("customer_snapshots");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Email).HasMaxLength(CustomerSnapshot.EmailMaxLength).IsRequired();
        builder.Property(c => c.DisplayName).HasMaxLength(CustomerSnapshot.DisplayNameMaxLength);
        builder.Property(c => c.UpdatedAt).IsRequired();
    }
}
