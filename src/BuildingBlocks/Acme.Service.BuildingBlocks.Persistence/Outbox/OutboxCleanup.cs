using Acme.Service.BuildingBlocks.Persistence.Inbox;
using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// Retention of the outbox and inbox tables of every registered module. Run hourly by the recurring
/// <see cref="OutboxCleanupJob"/>; nothing here runs on its own.
/// </summary>
/// <remarks>
/// Only processed outbox messages are deleted; dead ones stay for inspection and manual replay. Inbox rows
/// must outlive any possible redelivery (<see cref="InboxOptions.RetentionDays"/> well above the outbox retry window).
/// </remarks>
public sealed class OutboxCleanup(
    IEnumerable<MigrationTarget> modules,
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> outboxOptions,
    IOptions<InboxOptions> inboxOptions,
    TimeProvider timeProvider) : IMessageRetention
{
    private readonly IReadOnlyList<MigrationTarget> _modules = [.. modules];

    /// <summary>
    /// Deletes outbox messages processed more than <see cref="OutboxOptions.RetentionDays"/> ago, in every
    /// module. Returns the number of rows deleted.
    /// </summary>
    public Task<int> DeleteProcessedOutboxMessagesAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-outboxOptions.Value.RetentionDays);
        return ForEachModuleAsync(
            (context, token) => context.Set<OutboxMessage>()
                .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
                .ExecuteDeleteAsync(token),
            cancellationToken);
    }

    /// <summary>
    /// Deletes inbox rows recorded more than <see cref="InboxOptions.RetentionDays"/> ago, in every module.
    /// Returns the number of rows deleted.
    /// </summary>
    public Task<int> DeleteExpiredInboxMessagesAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-inboxOptions.Value.RetentionDays);
        return ForEachModuleAsync(
            (context, token) => context.Set<InboxMessage>()
                .Where(m => m.ProcessedAt < cutoff)
                .ExecuteDeleteAsync(token),
            cancellationToken);
    }

    private async Task<int> ForEachModuleAsync(Func<DbContext, CancellationToken, Task<int>> delete, CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var module in _modules)
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var context = (DbContext)scope.ServiceProvider.GetRequiredService(module.ContextType);
                total += await delete(context, cancellationToken).ConfigureAwait(false);
            }
        }

        return total;
    }
}
