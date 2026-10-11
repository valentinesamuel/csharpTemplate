using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Jobs;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.InMemory;

/// <summary>
/// Development <see cref="IJobQueue"/> (<c>Jobs:Provider=InMemory</c>, rejected in Production by D-21). Jobs go
/// into an in-process <see cref="Channel{T}"/> drained by <see cref="InMemoryJobWorker"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Dev-only: queued, scheduled and recurring jobs live only in memory and are lost on restart; every replica
/// runs its own recurring schedules.</item>
/// <item>Payloads are serialized with System.Text.Json at enqueue time (like a persistent provider), so the job sees
/// a copy and non-serializable payloads fail at the call site.</item>
/// <item>Job instances are resolved from a new DI scope per run (registered service or
/// <see cref="ActivatorUtilities"/>), and the caller's correlation id is restored for the run (D-25).</item>
/// <item>Recurring jobs use Cronos: 5-field cron, or 6 fields with leading seconds, evaluated in UTC.</item>
/// </list>
/// </remarks>
internal sealed class InMemoryJobQueue : IJobQueue, IDisposable
{
    private readonly Channel<JobWorkItem> _channel = Channel.CreateUnbounded<JobWorkItem>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _recurringGate = new();
    private readonly Dictionary<string, CancellationTokenSource> _recurring = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly ICorrelationContext? _correlation;
    private bool _disposed;

    public InMemoryJobQueue(TimeProvider timeProvider, ICorrelationContext? correlation = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _correlation = correlation;
    }

    /// <summary>The queue drained by the worker.</summary>
    internal ChannelReader<JobWorkItem> Reader => _channel.Reader;

    /// <inheritdoc />
    public Task<string> EnqueueAsync<TJob, TPayload>(TPayload payload, CancellationToken cancellationToken)
        where TJob : IJob<TPayload> =>
        ScheduleAsync<TJob, TPayload>(payload, TimeSpan.Zero, cancellationToken);

    /// <inheritdoc />
    public Task<string> ScheduleAsync<TJob, TPayload>(TPayload payload, TimeSpan delay, CancellationToken cancellationToken)
        where TJob : IJob<TPayload>
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(payload);
        var item = new JobWorkItem(
            NewId(),
            typeof(TJob).Name,
            Attempt: 1,
            CurrentCorrelationId(),
            (services, ct) => ActivatorUtilities.GetServiceOrCreateInstance<TJob>(services)
                .ExecuteAsync(JsonSerializer.Deserialize<TPayload>(json)!, ct));

        Schedule(item, delay);
        return Task.FromResult(item.Id);
    }

    /// <inheritdoc />
    public Task AddOrUpdateRecurringAsync<TJob>(string recurringJobId, string cronExpression, CancellationToken cancellationToken)
        where TJob : IRecurringJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurringJobId);
        var cron = ParseCron(cronExpression);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_recurringGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_recurring.Remove(recurringJobId, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            var loop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _recurring[recurringJobId] = loop;
            _ = RunRecurringAsync<TJob>(recurringJobId, cron, loop.Token);
        }

        return Task.CompletedTask;
    }

    /// <summary>Queues <paramref name="item"/> after <paramref name="delay"/> (immediately when zero). Used for retries too.</summary>
    internal void Schedule(JobWorkItem item, TimeSpan delay)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (delay <= TimeSpan.Zero)
        {
            _channel.Writer.TryWrite(item);
            return;
        }

        _ = DelayThenWriteAsync(item, delay, _shutdown.Token);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_recurringGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var loop in _recurring.Values)
            {
                loop.Dispose();
            }

            _recurring.Clear();
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
        _channel.Writer.TryComplete();
    }

    internal static CronExpression ParseCron(string cronExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);

        var fields = cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        try
        {
            return CronExpression.Parse(cronExpression, fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            throw new ArgumentException($"Invalid cron expression: {ex.Message}", nameof(cronExpression), ex);
        }
    }

    private async Task DelayThenWriteAsync(JobWorkItem item, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            _channel.Writer.TryWrite(item);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: scheduled jobs are dropped (dev-only provider).
        }
    }

    private async Task RunRecurringAsync<TJob>(string recurringJobId, CronExpression cron, CancellationToken cancellationToken)
        where TJob : IRecurringJob
    {
        try
        {
            var last = _timeProvider.GetUtcNow();
            while (!cancellationToken.IsCancellationRequested)
            {
                // Cronos excludes the start instant, so basing on the last occurrence never fires it twice even if
                // the timer woke slightly early.
                var now = _timeProvider.GetUtcNow();
                var from = now > last ? now : last;
                var next = cron.GetNextOccurrence(from, TimeZoneInfo.Utc);
                if (next is null)
                {
                    return;
                }

                await Task.Delay(next.Value - now, _timeProvider, cancellationToken).ConfigureAwait(false);
                last = next.Value;

                _channel.Writer.TryWrite(new JobWorkItem(
                    NewId(),
                    $"{typeof(TJob).Name} ({recurringJobId})",
                    Attempt: 1,
                    CorrelationId: null,
                    (services, ct) => ActivatorUtilities.GetServiceOrCreateInstance<TJob>(services).ExecuteAsync(ct)));
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced by AddOrUpdateRecurringAsync, or shutting down.
        }
    }

    private string NewId() =>
        Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString("N", CultureInfo.InvariantCulture);

    private string? CurrentCorrelationId()
    {
        var correlationId = _correlation?.CorrelationId;
        if (!string.IsNullOrEmpty(correlationId))
        {
            return correlationId;
        }

        return Activity.Current is { } activity ? activity.TraceId.ToHexString() : null;
    }
}

/// <summary>One queued job run.</summary>
/// <param name="Id">Job id returned to the caller (retries keep it).</param>
/// <param name="JobName">Job type name, for logs.</param>
/// <param name="Attempt">1 for the first run, incremented per retry.</param>
/// <param name="CorrelationId">Correlation id captured at enqueue time, restored for the run.</param>
/// <param name="Execute">Resolves the job from the run's scope and invokes it.</param>
internal sealed record JobWorkItem(
    string Id,
    string JobName,
    int Attempt,
    string? CorrelationId,
    Func<IServiceProvider, CancellationToken, Task> Execute);

/// <summary>Code-level settings of the in-memory job queue (no configuration keys; §0.7 defines none).</summary>
internal sealed class InMemoryJobQueueOptions
{
    /// <summary>Retries after the first failed run (plan P7: 3). A job runs at most <c>MaxRetries + 1</c> times.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Delay before each retry.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Jobs run concurrently by the worker.</summary>
    public int WorkerCount { get; set; } = 4;

    /// <summary>Registers the options with validation on start.</summary>
    public static void Register(IServiceCollection services) =>
        services.AddOptions<InMemoryJobQueueOptions>()
            .Validate(o => o.MaxRetries >= 0, "In-memory jobs: MaxRetries must not be negative.")
            .Validate(o => o.RetryDelay >= TimeSpan.Zero, "In-memory jobs: RetryDelay must not be negative.")
            .Validate(o => o.WorkerCount >= 1, "In-memory jobs: WorkerCount must be at least 1.")
            .ValidateOnStart();
}
