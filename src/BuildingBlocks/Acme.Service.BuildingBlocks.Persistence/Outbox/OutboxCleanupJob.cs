using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Locking;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// Recurring retention job for the outbox and inbox tables of every registered module (via <see cref="OutboxCleanup"/>:
/// processed outbox rows older than <c>Outbox:RetentionDays</c>, inbox rows older than <c>Inbox:RetentionDays</c>).
/// Scheduled through the provider-neutral <see cref="IJobQueue"/> by <see cref="OutboxCleanupScheduler"/> as
/// <see cref="RecurringJobId"/> on <see cref="CronExpression"/>.
/// </summary>
/// <remarks>
/// The job queue enqueues each occurrence once, but a slow run could still overlap the next one (or, with the
/// in-memory provider, every replica runs its own schedule), so the run is guarded by the distributed lock
/// <see cref="LockName"/>; a run that cannot take it at once is skipped.
/// </remarks>
internal sealed partial class OutboxCleanupJob(
    IMessageRetention retention,
    IDistributedLock distributedLock,
    ILogger<OutboxCleanupJob> logger) : IRecurringJob
{
    /// <summary>Recurring job id.</summary>
    public const string RecurringJobId = "outbox-cleanup";

    /// <summary>Schedule: hourly, at minute 0 (UTC).</summary>
    public const string CronExpression = "0 * * * *";

    /// <summary>Distributed lock that prevents overlapping runs.</summary>
    public const string LockName = "outbox-cleanup";

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var handle = await distributedLock.TryAcquireAsync(LockName, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            LogSkipped(logger);
            return;
        }

        await using (handle.ConfigureAwait(false))
        {
            var outbox = await retention.DeleteProcessedOutboxMessagesAsync(cancellationToken).ConfigureAwait(false);
            var inbox = await retention.DeleteExpiredInboxMessagesAsync(cancellationToken).ConfigureAwait(false);
            LogCompleted(logger, outbox, inbox);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox cleanup skipped: another run holds the lock")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Outbox cleanup deleted {OutboxDeleted} processed outbox message(s) and {InboxDeleted} inbox message(s)")]
    private static partial void LogCompleted(ILogger logger, int outboxDeleted, int inboxDeleted);
}

/// <summary>The retention operations used by <see cref="OutboxCleanupJob"/> (implemented by <see cref="OutboxCleanup"/>).</summary>
internal interface IMessageRetention
{
    /// <summary>Deletes processed outbox messages past retention in every module; returns the rows deleted.</summary>
    Task<int> DeleteProcessedOutboxMessagesAsync(CancellationToken cancellationToken);

    /// <summary>Deletes inbox rows past retention in every module; returns the rows deleted.</summary>
    Task<int> DeleteExpiredInboxMessagesAsync(CancellationToken cancellationToken);
}
