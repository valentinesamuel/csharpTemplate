using System.Text.Json;
using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Infra.Hangfire;

/// <summary>
/// The Hangfire job behind every <see cref="IJobQueue.EnqueueAsync{TJob,TPayload}"/> and
/// <see cref="IJobQueue.ScheduleAsync{TJob,TPayload}"/> call. Hangfire persists this closed generic type (with the job
/// and payload type names) and <see cref="RunAsync"/>'s arguments: the payload as a System.Text.Json string and the
/// <see cref="HangfireJobMetadata"/>. Each run opens its own DI scope, restores the enqueue-time correlation id (D-25),
/// and resolves and invokes <typeparamref name="TJob"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The job and payload types are type arguments of the runner, not of the method: Hangfire can only recover
/// method type arguments that appear in the parameter types, but it stores closed generic types completely.</item>
/// <item>Job exceptions propagate to Hangfire, which retries with its default policy (10 attempts with increasing
/// delays) and then moves the job to the Failed state. The host is never stopped by a job.</item>
/// <item>Persisted jobs reference CLR type names. Renaming or moving a job or payload type (including the template's init
/// rename) orphans already-persisted jobs of that type; the template ships with none, and deployments should drain the
/// queues before such a rename.</item>
/// </list>
/// Public only because Hangfire requires public job types and methods; not for direct use.
/// </remarks>
/// <typeparam name="TJob">The module job.</typeparam>
/// <typeparam name="TPayload">Its payload.</typeparam>
public sealed class HangfireJobRunner<TJob, TPayload>(IServiceScopeFactory scopeFactory, ILogger<HangfireJobRunner<TJob, TPayload>> logger)
    where TJob : IJob<TPayload>
{
    /// <summary>Runs <typeparamref name="TJob"/> with the deserialized <paramref name="payload"/>.</summary>
    /// <param name="payload">The payload as System.Text.Json.</param>
    /// <param name="metadata">Enqueue-time metadata (correlation id).</param>
    /// <param name="cancellationToken">Supplied by Hangfire: signalled on server shutdown or job deletion.</param>
    public Task RunAsync(string payload, HangfireJobMetadata? metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return HangfireJobScope.RunAsync(
            scopeFactory,
            logger,
            typeof(TJob).Name,
            metadata?.CorrelationId,
            (services, ct) => ActivatorUtilities.GetServiceOrCreateInstance<TJob>(services)
                .ExecuteAsync(JsonSerializer.Deserialize<TPayload>(payload)!, ct),
            cancellationToken);
    }
}

/// <summary>
/// The Hangfire job behind every <see cref="IJobQueue.AddOrUpdateRecurringAsync{TJob}"/> registration: each occurrence
/// opens its own DI scope and runs <typeparamref name="TJob"/>. Public only because Hangfire requires it.
/// </summary>
/// <typeparam name="TJob">The module's recurring job.</typeparam>
public sealed class HangfireRecurringJobRunner<TJob>(IServiceScopeFactory scopeFactory, ILogger<HangfireRecurringJobRunner<TJob>> logger)
    where TJob : IRecurringJob
{
    /// <summary>Runs one occurrence of <typeparamref name="TJob"/>.</summary>
    /// <param name="cancellationToken">Supplied by Hangfire: signalled on server shutdown.</param>
    public Task RunAsync(CancellationToken cancellationToken) =>
        HangfireJobScope.RunAsync(
            scopeFactory,
            logger,
            typeof(TJob).Name,
            correlationId: null,
            (services, ct) => ActivatorUtilities.GetServiceOrCreateInstance<TJob>(services).ExecuteAsync(ct),
            cancellationToken);
}

/// <summary>Metadata stored with each enqueued or scheduled job.</summary>
/// <param name="CorrelationId">The correlation id at enqueue time, restored for the run; null when none was in scope.</param>
public sealed record HangfireJobMetadata(string? CorrelationId);

/// <summary>Per-run DI scope and correlation handling shared by the runners.</summary>
internal static partial class HangfireJobScope
{
    private const string CorrelationIdProperty = LogPropertyNames.CorrelationId;

    // An async method: the correlation id set here (AsyncLocal) does not leak back into Hangfire's worker.
    public static async Task RunAsync(
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        string jobName,
        string? correlationId,
        Func<IServiceProvider, CancellationToken, Task> execute,
        CancellationToken cancellationToken)
    {
        // No enqueue-time id (recurring runs, or none was in scope): generate one per attempt (D-07 A), so every run
        // logs with a correlation id and downstream work sees the same value.
        var effective = string.IsNullOrEmpty(correlationId) ? CorrelationIdGenerator.Generate() : correlationId;
        using var logScope = logger.BeginScope(new Dictionary<string, object> { [CorrelationIdProperty] = effective });

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var correlation = scope.ServiceProvider.GetService<ICorrelationContext>();
            var previous = correlation?.CorrelationId;
            if (correlation is not null)
            {
                correlation.CorrelationId = effective;
            }

            try
            {
                await execute(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (correlation is not null)
                {
                    correlation.CorrelationId = previous;
                }
            }
        }

        LogCompleted(logger, jobName);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Job {JobName} completed")]
    private static partial void LogCompleted(ILogger logger, string jobName);
}
