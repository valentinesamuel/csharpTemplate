using System.Diagnostics;
using System.Text.Json;
using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Jobs;
using Hangfire;

namespace Acme.Service.BuildingBlocks.Infra.Hangfire;

/// <summary>
/// <see cref="IJobQueue"/> on Hangfire with Postgres storage (<c>Jobs:Provider=Hangfire</c>). Jobs are persisted, run by
/// any replica's Hangfire server, and survive restarts.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Payloads are serialized with System.Text.Json at enqueue time, so non-serializable payloads fail at the call
/// site. The caller's correlation id (or the current trace id) is stored with the job and restored for the run.</item>
/// <item>Scheduled jobs never run before the delay has elapsed: Hangfire keeps schedule times in whole seconds
/// (truncated), so the due time is rounded up to the next second. They run at the first scheduler poll after that
/// (<see cref="HangfireOptions.SchedulePollingInterval"/>, 15 s by default).</item>
/// <item>Recurring jobs: 5-field cron, or 6 fields with leading seconds, evaluated in UTC. Calling
/// <see cref="AddOrUpdateRecurringAsync{TJob}"/> from every replica at startup is safe: Hangfire stores one recurring job
/// per id and enqueues each occurrence once. Overlapping runs of long jobs must still be guarded with
/// <c>IDistributedLock</c>.</item>
/// <item>The Hangfire client API is synchronous; each call is one short write to Postgres.</item>
/// </list>
/// </remarks>
internal sealed class HangfireJobQueue(
    IBackgroundJobClient client,
    IRecurringJobManager recurringJobs,
    TimeProvider timeProvider,
    ICorrelationContext? correlation = null) : IJobQueue
{
    /// <inheritdoc />
    public Task<string> EnqueueAsync<TJob, TPayload>(TPayload payload, CancellationToken cancellationToken)
        where TJob : IJob<TPayload>
    {
        cancellationToken.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(payload);
        var metadata = new HangfireJobMetadata(CurrentCorrelationId());
        var id = client.Enqueue<HangfireJobRunner<TJob, TPayload>>(r => r.RunAsync(json, metadata, CancellationToken.None));
        return Task.FromResult(id);
    }

    /// <inheritdoc />
    public Task<string> ScheduleAsync<TJob, TPayload>(TPayload payload, TimeSpan delay, CancellationToken cancellationToken)
        where TJob : IJob<TPayload>
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(payload);
        var metadata = new HangfireJobMetadata(CurrentCorrelationId());
        var enqueueAt = RoundUpToSecond(timeProvider.GetUtcNow() + delay);
        var id = client.Schedule<HangfireJobRunner<TJob, TPayload>>(r => r.RunAsync(json, metadata, CancellationToken.None), enqueueAt);
        return Task.FromResult(id);
    }

    /// <inheritdoc />
    public Task AddOrUpdateRecurringAsync<TJob>(string recurringJobId, string cronExpression, CancellationToken cancellationToken)
        where TJob : IRecurringJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurringJobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);
        cancellationToken.ThrowIfCancellationRequested();

        // Hangfire validates the expression and throws ArgumentException when it is invalid.
        recurringJobs.AddOrUpdate<HangfireRecurringJobRunner<TJob>>(
            recurringJobId,
            r => r.RunAsync(CancellationToken.None),
            cronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        return Task.CompletedTask;
    }

    /// <summary>Rounds up to a whole second, so Hangfire's truncated schedule score is never before <paramref name="value"/>.</summary>
    internal static DateTimeOffset RoundUpToSecond(DateTimeOffset value)
    {
        var remainder = value.UtcTicks % TimeSpan.TicksPerSecond;
        return remainder == 0 ? value : value.AddTicks(TimeSpan.TicksPerSecond - remainder);
    }

    private string? CurrentCorrelationId()
    {
        var correlationId = correlation?.CorrelationId;
        if (!string.IsNullOrEmpty(correlationId))
        {
            return correlationId;
        }

        return Activity.Current is { } activity ? activity.TraceId.ToHexString() : null;
    }
}
