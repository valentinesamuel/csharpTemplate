using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.InMemory;

/// <summary>
/// Drains <see cref="InMemoryJobQueue"/> with <see cref="InMemoryJobQueueOptions.WorkerCount"/> concurrent loops.
/// Each run gets its own DI scope. A failing job is logged and retried <see cref="InMemoryJobQueueOptions.MaxRetries"/>
/// times after <see cref="InMemoryJobQueueOptions.RetryDelay"/>, then logged as failed and dropped. Job exceptions
/// never escape the worker, so they never stop the host.
/// </summary>
internal sealed partial class InMemoryJobWorker(
    InMemoryJobQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<InMemoryJobQueueOptions> options,
    ILogger<InMemoryJobWorker> logger) : BackgroundService
{
    private const string CorrelationIdProperty = LogPropertyNames.CorrelationId;

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        return Task.WhenAll(Enumerable.Range(0, settings.WorkerCount).Select(_ => RunLoopAsync(settings, stoppingToken)));
    }

    private async Task RunLoopAsync(InMemoryJobQueueOptions settings, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunAsync(item, settings, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host stopping; queued jobs are lost (dev-only provider).
        }
    }

    // A separate async method: the correlation id set here (AsyncLocal) does not leak back into the loop.
    private async Task RunAsync(JobWorkItem item, InMemoryJobQueueOptions settings, CancellationToken stoppingToken)
    {
        // No enqueue-time id (recurring runs, or none was in scope): generate one at run time; retries carry it.
        var effective = string.IsNullOrEmpty(item.CorrelationId) ? CorrelationIdGenerator.Generate() : item.CorrelationId;
        using var logScope = logger.BeginScope(new Dictionary<string, object> { [CorrelationIdProperty] = effective });

        try
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                if (scope.ServiceProvider.GetService<ICorrelationContext>() is { } correlation)
                {
                    correlation.CorrelationId = effective;
                }

                await item.Execute(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
            }

            LogCompleted(logger, item.JobName, item.Id, item.Attempt);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogAbandoned(logger, item.JobName, item.Id);
        }
        catch (Exception ex)
        {
            if (item.Attempt <= settings.MaxRetries)
            {
                LogRetrying(logger, ex, item.JobName, item.Id, item.Attempt, settings.RetryDelay);
                TrySchedule(item with { Attempt = item.Attempt + 1, CorrelationId = effective }, settings.RetryDelay);
            }
            else
            {
                LogFailed(logger, ex, item.JobName, item.Id, item.Attempt);
            }
        }
    }

    private void TrySchedule(JobWorkItem item, TimeSpan delay)
    {
        try
        {
            queue.Schedule(item, delay);
        }
        catch (ObjectDisposedException)
        {
            LogAbandoned(logger, item.JobName, item.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Job {JobName} {JobId} completed on attempt {Attempt}")]
    private static partial void LogCompleted(ILogger logger, string jobName, string jobId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobName} {JobId} failed on attempt {Attempt}; retrying in {RetryDelay}")]
    private static partial void LogRetrying(ILogger logger, Exception exception, string jobName, string jobId, int attempt, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {JobName} {JobId} failed on attempt {Attempt}; no retries left, the job is dropped")]
    private static partial void LogFailed(ILogger logger, Exception exception, string jobName, string jobId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobName} {JobId} abandoned because the host is stopping")]
    private static partial void LogAbandoned(ILogger logger, string jobName, string jobId);
}
