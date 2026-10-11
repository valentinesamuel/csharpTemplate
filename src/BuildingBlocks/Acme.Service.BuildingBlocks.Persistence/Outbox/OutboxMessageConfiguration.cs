using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// Maps <see cref="OutboxMessage"/> to <c>outbox_messages</c> in the module's default schema. Applied by
/// <see cref="ModuleDbContext"/>; a context that cannot derive from it (Auth, P11) applies it itself.
/// </summary>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    /// <summary>Table name.</summary>
    public const string TableName = "outbox_messages";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(TableName);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();

        // Dispatcher polling filter: processed_at IS NULL AND dead_at IS NULL AND next_attempt_at <= @now.
        builder.HasIndex(m => new { m.ProcessedAt, m.DeadAt, m.NextAttemptAt });
    }
}
