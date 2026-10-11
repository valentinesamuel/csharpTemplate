using Acme.Service.BuildingBlocks.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// Registers <see cref="OutboxCleanupJob"/> with the selected <see cref="IJobQueue"/> when the app starts (one hosted
/// service, however many modules). Calling it from every replica is safe: providers keep one recurring job per id.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Runs off the startup path, so a slow or unavailable job store never delays or fails startup.</item>
/// <item>A failure (for example the Hangfire schema is missing because <c>migrate</c> has not run yet) is logged and
/// retried every <see cref="RetryDelay"/> until it succeeds or the host stops.</item>
/// <item>Without a registered <see cref="IJobQueue"/> (the <c>migrate</c> host, test hosts) it does nothing.</item>
/// </list>
/// </remarks>
internal sealed partial class OutboxCleanupScheduler(
    IServiceProvider services,
    TimeProvider timeProvider,
    ILogger<OutboxCleanupScheduler> logger) : BackgroundService
{
    /// <summary>Delay between registration attempts after a failure. Default 1 minute.</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave the synchronous part of StartAsync at once: some providers' client APIs block on I/O.
        await Task.Yield();

        var queue = services.GetService<IJobQueue>();
        if (queue is null)
        {
            LogNoJobQueue(logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.AddOrUpdateRecurringAsync<OutboxCleanupJob>(
                    OutboxCleanupJob.RecurringJobId,
                    OutboxCleanupJob.CronExpression,
                    stoppingToken).ConfigureAwait(false);
                LogScheduled(logger, OutboxCleanupJob.RecurringJobId, OutboxCleanupJob.CronExpression);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogScheduleFailed(logger, ex, OutboxCleanupJob.RecurringJobId, RetryDelay);
            }

            try
            {
                await Task.Delay(RetryDelay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No job queue registered; the outbox cleanup job is not scheduled")]
    private static partial void LogNoJobQueue(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recurring job {RecurringJobId} scheduled on {CronExpression}")]
    private static partial void LogScheduled(ILogger logger, string recurringJobId, string cronExpression);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not schedule recurring job {RecurringJobId}; retrying in {RetryDelay}")]
    private static partial void LogScheduleFailed(ILogger logger, Exception exception, string recurringJobId, TimeSpan retryDelay);
}
