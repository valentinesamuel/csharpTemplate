using System.Runtime.CompilerServices;
using Acme.Service.BuildingBlocks.Persistence.Inbox;
using Acme.Service.BuildingBlocks.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Acme.Service.BuildingBlocks.Persistence.Messaging;

/// <summary>
/// Raw SQL of the outbox/inbox statements that LINQ cannot express (<c>FOR UPDATE SKIP LOCKED</c>,
/// <c>ON CONFLICT DO NOTHING</c>). Table and column identifiers are taken from the context's EF model and
/// quoted by the provider, never from input; every value is a named parameter.
/// </summary>
/// <param name="SelectOutboxBatch">Locks the next batch; parameters <c>@now</c> and <c>@batch</c>.</param>
/// <param name="InsertInbox">Records a handled message; parameters <c>@message_id</c>, <c>@consumer</c>, <c>@processed_at</c>.</param>
internal sealed record MessagingSql(string SelectOutboxBatch, string InsertInbox)
{
    private static readonly ConditionalWeakTable<IModel, MessagingSql> _cache = [];

    public static MessagingSql For(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _cache.GetValue(context.Model, _ => Build(context));
    }

    private static MessagingSql Build(DbContext context)
    {
        var sql = context.GetService<ISqlGenerationHelper>();

        var outbox = Table(context, typeof(OutboxMessage), out var outboxTable);
        var id = Column(outbox, outboxTable, nameof(OutboxMessage.Id), sql);
        var processedAt = Column(outbox, outboxTable, nameof(OutboxMessage.ProcessedAt), sql);
        var deadAt = Column(outbox, outboxTable, nameof(OutboxMessage.DeadAt), sql);
        var nextAttemptAt = Column(outbox, outboxTable, nameof(OutboxMessage.NextAttemptAt), sql);

        var inbox = Table(context, typeof(InboxMessage), out var inboxTable);
        var messageId = Column(inbox, inboxTable, nameof(InboxMessage.MessageId), sql);
        var consumer = Column(inbox, inboxTable, nameof(InboxMessage.Consumer), sql);
        var inboxProcessedAt = Column(inbox, inboxTable, nameof(InboxMessage.ProcessedAt), sql);

        var select =
            $"SELECT * FROM {sql.DelimitIdentifier(outboxTable.Name, outboxTable.Schema)} " +
            $"WHERE {processedAt} IS NULL AND {deadAt} IS NULL AND {nextAttemptAt} <= @now " +
            $"ORDER BY {id} LIMIT @batch FOR UPDATE SKIP LOCKED";

        var insert =
            $"INSERT INTO {sql.DelimitIdentifier(inboxTable.Name, inboxTable.Schema)} ({messageId}, {consumer}, {inboxProcessedAt}) " +
            "VALUES (@message_id, @consumer, @processed_at) ON CONFLICT DO NOTHING";

        return new MessagingSql(select, insert);
    }

    private static IEntityType Table(DbContext context, Type entityType, out StoreObjectIdentifier table)
    {
        var entity = context.Model.FindEntityType(entityType)
            ?? throw new InvalidOperationException(
                $"{context.GetType().Name} does not map {entityType.Name}. Derive from {nameof(ModuleDbContext)} or apply its configuration.");

        table = StoreObjectIdentifier.Table(
            entity.GetTableName() ?? throw new InvalidOperationException($"{entityType.Name} is not mapped to a table."),
            entity.GetSchema());
        return entity;
    }

    private static string Column(IEntityType entity, StoreObjectIdentifier table, string property, ISqlGenerationHelper sql) =>
        sql.DelimitIdentifier(entity.FindProperty(property)?.GetColumnName(table)
            ?? throw new InvalidOperationException($"{entity.DisplayName()}.{property} is not mapped to a column."));
}

/// <summary>Access to <see cref="MessagingSql"/> for a module context.</summary>
internal static class MessagingSqlExtensions
{
    public static MessagingSql MessagingSql(this DbContext context) => Messaging.MessagingSql.For(context);
}
