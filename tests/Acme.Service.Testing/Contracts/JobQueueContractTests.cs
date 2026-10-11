using System.Collections.Concurrent;
using System.Diagnostics;
using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Acme.Service.Testing.Contracts;

/// <summary>
/// Behaviour every <see cref="IJobQueue"/> provider must have (P7; reused by Hangfire in P10). Each test runs a
/// generic host with the provider from <see cref="ConfigureProvider"/>, a <see cref="JobContractProbe"/> that the
/// contract jobs report to, a scoped <see cref="JobContractScopedDependency"/>, and an AsyncLocal
/// <see cref="ICorrelationContext"/> (unless the provider registers its own). Markers are unique per test, so a
/// shared job store is fine. Providers with polling schedulers raise the timeouts.
/// </summary>
public abstract class JobQueueContractTests : IAsyncLifetime
{
    private IHost? _host;

    /// <summary>The probe the contract jobs report to.</summary>
    protected JobContractProbe Probe { get; } = new();

    /// <summary>The running host.</summary>
    protected IHost TestHost => _host ?? throw new InvalidOperationException("The host is not started.");

    /// <summary>The queue under test.</summary>
    protected IJobQueue Queue => TestHost.Services.GetRequiredService<IJobQueue>();

    /// <summary>How long to wait for an enqueued or scheduled job to run.</summary>
    protected virtual TimeSpan ExecutionTimeout => TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for the first run of an every-second recurring job.</summary>
    protected virtual TimeSpan RecurringTimeout => TimeSpan.FromSeconds(10);

    /// <summary>Registers the provider under test (services, configuration and environment are available).</summary>
    protected abstract void ConfigureProvider(IHostApplicationBuilder builder);

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Services.AddLogging();
        builder.Services.AddSingleton(Probe);
        builder.Services.AddScoped<JobContractScopedDependency>();
        ConfigureProvider(builder);
        builder.Services.TryAddSingleton<ICorrelationContext, JobContractCorrelationContext>();

        _host = builder.Build();
        await _host.StartAsync(Ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _host.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task EnqueueAsync_Job_RunsWithPayload()
    {
        var payload = NewPayload();

        var id = await Queue.EnqueueAsync<JobContractEchoJob, JobContractPayload>(payload, Ct);

        id.ShouldNotBeNullOrWhiteSpace();
        var run = (await Probe.WaitForAsync(payload.Marker, 1, ExecutionTimeout, Ct)).ShouldHaveSingleItem();
        run.Payload.ShouldNotBeNull();
        run.Payload.Number.ShouldBe(payload.Number);
        run.Payload.Tags.ShouldBe(payload.Tags);
    }

    [Fact]
    public async Task EnqueueAsync_TwoJobs_RunInSeparateScopes()
    {
        var first = NewPayload();
        var second = NewPayload();

        await Queue.EnqueueAsync<JobContractEchoJob, JobContractPayload>(first, Ct);
        await Queue.EnqueueAsync<JobContractEchoJob, JobContractPayload>(second, Ct);

        var firstRun = (await Probe.WaitForAsync(first.Marker, 1, ExecutionTimeout, Ct)).ShouldHaveSingleItem();
        var secondRun = (await Probe.WaitForAsync(second.Marker, 1, ExecutionTimeout, Ct)).ShouldHaveSingleItem();
        firstRun.ScopeId.ShouldNotBe(secondRun.ScopeId);
    }

    [Fact]
    public async Task EnqueueAsync_CorrelationIdInScope_JobSeesIt()
    {
        var payload = NewPayload();
        var correlation = TestHost.Services.GetRequiredService<ICorrelationContext>();
        var correlationId = "contract-" + Guid.NewGuid().ToString("N");

        correlation.CorrelationId = correlationId;
        try
        {
            await Queue.EnqueueAsync<JobContractEchoJob, JobContractPayload>(payload, Ct);
        }
        finally
        {
            correlation.CorrelationId = null;
        }

        var run = (await Probe.WaitForAsync(payload.Marker, 1, ExecutionTimeout, Ct)).ShouldHaveSingleItem();
        run.CorrelationId.ShouldBe(correlationId);
    }

    [Fact]
    public async Task ScheduleAsync_Delay_RunsNotBeforeDelay()
    {
        var payload = NewPayload();
        var delay = TimeSpan.FromSeconds(1);
        var scheduledAt = Stopwatch.GetTimestamp();

        await Queue.ScheduleAsync<JobContractEchoJob, JobContractPayload>(payload, delay, Ct);

        var run = (await Probe.WaitForAsync(payload.Marker, 1, delay + ExecutionTimeout, Ct)).ShouldHaveSingleItem();
        Stopwatch.GetElapsedTime(scheduledAt, run.Timestamp).ShouldBeGreaterThanOrEqualTo(delay - TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task ScheduleAsync_NegativeDelay_ThrowsArgumentOutOfRange() =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => Queue.ScheduleAsync<JobContractEchoJob, JobContractPayload>(NewPayload(), TimeSpan.FromSeconds(-1), Ct));

    [Fact]
    public async Task EnqueueAsync_FailingJob_HostKeepsRunningJobs()
    {
        var failing = NewPayload();
        var healthy = NewPayload();
        var lifetime = TestHost.Services.GetRequiredService<IHostApplicationLifetime>();

        await Queue.EnqueueAsync<JobContractFailingJob, JobContractPayload>(failing, Ct);
        await Probe.WaitForAsync(failing.Marker, 1, ExecutionTimeout, Ct);
        await Queue.EnqueueAsync<JobContractEchoJob, JobContractPayload>(healthy, Ct);

        (await Probe.WaitForAsync(healthy.Marker, 1, ExecutionTimeout, Ct)).ShouldHaveSingleItem();
        lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task AddOrUpdateRecurringAsync_EverySecond_Runs()
    {
        var id = "contract-" + Guid.NewGuid().ToString("N");

        await Queue.AddOrUpdateRecurringAsync<JobContractRecurringJob>(id, "* * * * * *", Ct);

        (await Probe.WaitForAsync(JobContractRecurringJob.Marker, 1, RecurringTimeout, Ct)).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("not a cron")]
    [InlineData("61 * * * *")]
    [InlineData("")]
    public async Task AddOrUpdateRecurringAsync_InvalidCron_ThrowsArgumentException(string cron)
    {
        var exception = await Should.ThrowAsync<Exception>(
            () => Queue.AddOrUpdateRecurringAsync<JobContractRecurringJob>("contract-invalid", cron, Ct));

        exception.ShouldBeAssignableTo<ArgumentException>();
    }

    /// <summary>The current test's cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A payload with a unique marker.</summary>
    protected static JobContractPayload NewPayload() =>
        new(Guid.NewGuid().ToString("N"), Random.Shared.Next(1, 1000), ["a", "b"]);
}

/// <summary>Payload of the contract jobs.</summary>
/// <param name="Marker">Unique per test; the probe groups runs by it.</param>
/// <param name="Number">Round-trip check.</param>
/// <param name="Tags">Round-trip check of a collection.</param>
public sealed record JobContractPayload(string Marker, int Number, IReadOnlyList<string> Tags);

/// <summary>One observed job run.</summary>
/// <param name="Payload">The payload the job received (null for recurring jobs).</param>
/// <param name="ScopeId">Id of the scoped dependency the job received.</param>
/// <param name="CorrelationId">The correlation id visible to the job.</param>
/// <param name="Timestamp"><see cref="Stopwatch.GetTimestamp"/> at the start of the run.</param>
public sealed record JobContractRun(JobContractPayload? Payload, Guid ScopeId, string? CorrelationId, long Timestamp);

/// <summary>Collects job runs by marker.</summary>
public sealed class JobContractProbe
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<JobContractRun>> _runs = new(StringComparer.Ordinal);

    /// <summary>Records a run under <paramref name="marker"/>.</summary>
    public void Record(string marker, JobContractRun run) =>
        _runs.GetOrAdd(marker, _ => new ConcurrentQueue<JobContractRun>()).Enqueue(run);

    /// <summary>The runs recorded so far under <paramref name="marker"/>.</summary>
    public IReadOnlyList<JobContractRun> Runs(string marker) =>
        _runs.TryGetValue(marker, out var runs) ? [.. runs] : [];

    /// <summary>Waits until at least <paramref name="count"/> runs exist under <paramref name="marker"/>, failing on timeout.</summary>
    public async Task<IReadOnlyList<JobContractRun>> WaitForAsync(string marker, int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var runs = Runs(marker);
            if (runs.Count >= count)
            {
                return runs;
            }

            if (elapsed.Elapsed > timeout)
            {
                throw new TimeoutException($"Expected {count} run(s) of '{marker}' within {timeout}; saw {runs.Count}.");
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>A scoped service; each job run must see its own instance.</summary>
public sealed class JobContractScopedDependency
{
    /// <summary>Instance id.</summary>
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>Records each run with its payload, scope and correlation id.</summary>
public sealed class JobContractEchoJob(
    JobContractProbe probe,
    JobContractScopedDependency scoped,
    ICorrelationContext correlation) : IJob<JobContractPayload>
{
    /// <inheritdoc />
    public Task ExecuteAsync(JobContractPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        probe.Record(payload.Marker, new JobContractRun(payload, scoped.Id, correlation.CorrelationId, Stopwatch.GetTimestamp()));
        return Task.CompletedTask;
    }
}

/// <summary>Records each attempt, then fails.</summary>
public sealed class JobContractFailingJob(JobContractProbe probe, JobContractScopedDependency scoped) : IJob<JobContractPayload>
{
    /// <inheritdoc />
    public Task ExecuteAsync(JobContractPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        probe.Record(payload.Marker, new JobContractRun(payload, scoped.Id, null, Stopwatch.GetTimestamp()));
        throw new InvalidOperationException("Contract test job failure.");
    }
}

/// <summary>Recurring contract job; records under <see cref="Marker"/>.</summary>
public sealed class JobContractRecurringJob(JobContractProbe probe, JobContractScopedDependency scoped) : IRecurringJob
{
    /// <summary>The probe marker of recurring runs.</summary>
    public const string Marker = "recurring";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        probe.Record(Marker, new JobContractRun(null, scoped.Id, null, Stopwatch.GetTimestamp()));
        return Task.CompletedTask;
    }
}

/// <summary>AsyncLocal correlation context used when the provider does not register one.</summary>
public sealed class JobContractCorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <inheritdoc />
    public string? CorrelationId
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
