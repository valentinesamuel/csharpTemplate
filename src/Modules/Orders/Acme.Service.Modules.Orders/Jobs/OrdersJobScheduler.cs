using Acme.Service.BuildingBlocks.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Orders.Jobs;

/// <summary>
/// Registers the Orders recurring job (<see cref="ExpireStalePendingOrdersJob"/>) with the selected
/// <see cref="IJobQueue"/> when the web app starts. Every replica may call it: providers keep one recurring job per id.
/// </summary>
/// <remarks>
/// Off the startup path, so a slow or unavailable job store never delays startup; a failure (for example the Hangfire
/// schema is missing until <c>migrate</c> runs) is logged and retried every <see cref="RetryDelay"/>. Without a
/// registered <see cref="IJobQueue"/> it does nothing.
/// </remarks>
internal sealed partial class OrdersJobScheduler(
    IServiceProvider services,
    TimeProvider timeProvider,
    ILogger<OrdersJobScheduler> logger) : BackgroundService
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
                await queue.AddOrUpdateRecurringAsync<ExpireStalePendingOrdersJob>(
                    ExpireStalePendingOrdersJob.RecurringJobId,
                    ExpireStalePendingOrdersJob.CronExpression,
                    stoppingToken).ConfigureAwait(false);
                LogScheduled(logger, ExpireStalePendingOrdersJob.RecurringJobId, ExpireStalePendingOrdersJob.CronExpression);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogScheduleFailed(logger, ex, ExpireStalePendingOrdersJob.RecurringJobId, RetryDelay);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "No job queue registered; the Orders recurring jobs are not scheduled")]
    private static partial void LogNoJobQueue(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recurring job {RecurringJobId} scheduled on {CronExpression}")]
    private static partial void LogScheduled(ILogger logger, string recurringJobId, string cronExpression);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not schedule recurring job {RecurringJobId}; retrying in {RetryDelay}")]
    private static partial void LogScheduleFailed(ILogger logger, Exception exception, string recurringJobId, TimeSpan retryDelay);
}
