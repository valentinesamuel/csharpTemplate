using Acme.Service.Modules.Orders.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.Modules.Orders.Persistence.Configurations;

/// <summary>
/// <c>orders.order_attachments(id, order_id, file_name, content_type, size_bytes, uploaded_by, created_at)</c>, with
/// <c>order_id</c> referencing <c>orders.orders</c> (same schema). The blob key is derived, not stored.
/// </summary>
internal sealed class OrderAttachmentConfiguration : IEntityTypeConfiguration<OrderAttachment>
{
    public void Configure(EntityTypeBuilder<OrderAttachment> builder)
    {
        builder.ToTable("order_attachments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.FileName).HasMaxLength(OrderAttachment.FileNameMaxLength).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(OrderAttachment.ContentTypeMaxLength).IsRequired();
        builder.Property(a => a.SizeBytes).IsRequired();
        builder.Property(a => a.UploadedBy).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Ignore(a => a.BlobKey);

        builder.HasOne<Order>().WithMany().HasForeignKey(a => a.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => a.OrderId);
    }
}
