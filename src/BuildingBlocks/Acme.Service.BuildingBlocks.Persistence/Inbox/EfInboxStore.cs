using Acme.Service.BuildingBlocks.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Acme.Service.BuildingBlocks.Persistence.Inbox;

/// <summary>Records handled messages in a module's <c>inbox_messages</c> table. Keyed by module name (D-04).</summary>
internal interface IInboxStore
{
    /// <summary>
    /// Inserts <c>(messageId, consumer)</c> in the current transaction. Returns false when the row already exists,
    /// meaning the consumer has already handled the message.
    /// </summary>
    Task<bool> TryRecordAsync(Guid messageId, string consumer, DateTimeOffset processedAt, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IInboxStore"/> over a module context. Uses <c>INSERT ... ON CONFLICT DO NOTHING</c>: a duplicate
/// (the unique violation of the primary key) is reported as "already processed" without raising 23505,
/// which would abort the enclosing Postgres transaction. A concurrent insert of the same key waits for the
/// other transaction and then reports a duplicate if it committed.
/// </summary>
/// <typeparam name="TContext">The consuming module's context.</typeparam>
internal sealed class EfInboxStore<TContext>(TContext context) : IInboxStore
    where TContext : ModuleDbContext
{
    public async Task<bool> TryRecordAsync(Guid messageId, string consumer, DateTimeOffset processedAt, CancellationToken cancellationToken)
    {
        // Identifiers come from the EF model (never from input); values are parameters.
        var sql = context.MessagingSql().InsertInbox;
        var inserted = await context.Database.ExecuteSqlRawAsync(
            sql,
            [
                new NpgsqlParameter("message_id", messageId),
                new NpgsqlParameter("consumer", consumer),
                new NpgsqlParameter("processed_at", processedAt.ToUniversalTime()),
            ],
            cancellationToken).ConfigureAwait(false);

        return inserted == 1;
    }
}
