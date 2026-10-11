using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.BuildingBlocks.Persistence.Inbox;

/// <summary>
/// Maps <see cref="InboxMessage"/> to <c>inbox_messages</c> (primary key <c>(message_id, consumer)</c>) in the
/// module's default schema. Applied by <see cref="ModuleDbContext"/>.
/// </summary>
public sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    /// <summary>Table name.</summary>
    public const string TableName = "inbox_messages";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(TableName);
        builder.HasKey(m => new { m.MessageId, m.Consumer });
        builder.Property(m => m.Consumer).IsRequired();
    }
}
