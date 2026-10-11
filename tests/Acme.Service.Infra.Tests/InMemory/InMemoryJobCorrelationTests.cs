using System.Collections.Concurrent;
using System.Diagnostics;
using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Infra.InMemory;
using Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;
using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.Infra.Tests.Fixtures;
using Acme.Service.Testing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Acme.Service.Infra.Tests.InMemory;

/// <summary>
/// Every in-memory job run logs with a correlation id (LOG-09): the enqueue-time id is restored unchanged, a run
/// without one gets a generated 32-hex id, and retries of one job share the id of its first attempt.
/// </summary>
public sealed partial class InMemoryJobCorrelationTests : IAsyncLifetime
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly ScopeRecordingLoggerProvider _logs = new();
    private readonly CorrelationProbe _probe = new();
    private IHost? _host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost TestHost => _host ?? throw new InvalidOperationException("The host is not started.");

    private IJobQueue Queue => TestHost.Services.GetRequiredService<IJobQueue>();

    public async ValueTask InitializeAsync()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSingleton(_probe);
        builder.Services.AddSingleton<ICorrelationContext, JobContractCorrelationContext>();
        new InMemoryJobsRegistration().Register(builder.Services, builder.Configuration);
        builder.Services.Configure<InMemoryJobQueueOptions>(o => o.RetryDelay = TimeSpan.FromMilliseconds(10));

        _host = builder.Build();
        await _host.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }

    [Fact]
    public async Task EnqueueAsync_NoAmbientCorrelationId_RunLogsAndSeesGeneratedId()
    {
        var marker = NewMarker();

        await Queue.EnqueueAsync<LoggingJob, CorrelationPayload>(new CorrelationPayload(marker, FailuresBeforeSuccess: 0), Ct);

        var run = (await _probe.WaitForAsync(marker, 1)).ShouldHaveSingleItem();
        run.ShouldNotBeNull().ShouldMatch("^[a-f0-9]{32}$");
        JobLines(marker).ShouldHaveSingleItem().CorrelationId.ShouldBe(run);
    }

    [Fact]
    public async Task AddOrUpdateRecurringAsync_Occurrence_RunLogsAndSeesGeneratedId()
    {
        await Queue.AddOrUpdateRecurringAsync<RecurringLoggingJob>("correlation-" + NewMarker(), "* * * * * *", Ct);

        var run = (await _probe.WaitForAsync(RecurringLoggingJob.Marker, 1))[0];
        run.ShouldNotBeNull().ShouldMatch("^[a-f0-9]{32}$");
        JobLines(RecurringLoggingJob.Marker)[0].CorrelationId.ShouldBe(run);
    }

    [Fact]
    public async Task EnqueueAsync_AmbientCorrelationId_RunLogsAndSeesItUnchanged()
    {
        var marker = NewMarker();
        var correlation = TestHost.Services.GetRequiredService<ICorrelationContext>();

        correlation.CorrelationId = "abc-123";
        try
        {
            await Queue.EnqueueAsync<LoggingJob, CorrelationPayload>(new CorrelationPayload(marker, FailuresBeforeSuccess: 0), Ct);
        }
        finally
        {
            correlation.CorrelationId = null;
        }

        (await _probe.WaitForAsync(marker, 1)).ShouldHaveSingleItem().ShouldBe("abc-123");
        JobLines(marker).ShouldHaveSingleItem().CorrelationId.ShouldBe("abc-123");
    }

    [Fact]
    public async Task EnqueueAsync_FailsTwiceThenSucceeds_AllAttemptsShareOneGeneratedId()
    {
        var marker = NewMarker();

        var jobId = await Queue.EnqueueAsync<LoggingJob, CorrelationPayload>(new CorrelationPayload(marker, FailuresBeforeSuccess: 2), Ct);

        var runs = await _probe.WaitForAsync(marker, 3);
        await WaitForAsync(() => WorkerLines(jobId).Any(e => e.Message.Contains("completed", StringComparison.Ordinal)));

        runs.Count.ShouldBe(3);
        var id = runs[0].ShouldNotBeNull();
        id.ShouldMatch("^[a-f0-9]{32}$");
        runs.ShouldAllBe(r => r == id);
        JobLines(marker).Count.ShouldBe(3);
        JobLines(marker).ShouldAllBe(e => e.CorrelationId == id);
        var worker = WorkerLines(jobId);
        worker.Count(e => e.Message.Contains("retrying", StringComparison.Ordinal)).ShouldBe(2);
        worker.Count(e => e.Message.Contains("completed", StringComparison.Ordinal)).ShouldBe(1);
        worker.ShouldAllBe(e => e.CorrelationId == id);
    }

    private static string NewMarker() => Guid.NewGuid().ToString("N");

    private List<ScopedLogEntry> JobLines(string marker) =>
        [.. _logs.Entries.Where(e => e.Message == "Correlation job ran for " + marker)];

    private List<ScopedLogEntry> WorkerLines(string jobId) =>
        [.. _logs.Entries.Where(e => e.Category == typeof(InMemoryJobWorker).FullName && e.Message.Contains(jobId, StringComparison.Ordinal))];

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            elapsed.Elapsed.ShouldBeLessThan(_timeout);
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>Payload of <see cref="LoggingJob"/>.</summary>
    internal sealed record CorrelationPayload(string Marker, int FailuresBeforeSuccess);

    /// <summary>Records the correlation id each run sees, by marker, and counts attempts.</summary>
    internal sealed class CorrelationProbe
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string?>> _runs = new(StringComparer.Ordinal);

        /// <summary>Records a run and returns its 1-based attempt number.</summary>
        public int Record(string marker, string? correlationId)
        {
            var runs = _runs.GetOrAdd(marker, _ => new ConcurrentQueue<string?>());
            runs.Enqueue(correlationId);
            return runs.Count;
        }

        public async Task<IReadOnlyList<string?>> WaitForAsync(string marker, int count)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                IReadOnlyList<string?> runs = _runs.TryGetValue(marker, out var queue) ? [.. queue] : [];
                if (runs.Count >= count)
                {
                    return runs;
                }

                elapsed.Elapsed.ShouldBeLessThan(_timeout, $"Expected {count} run(s) of '{marker}'; saw {runs.Count}.");
                await Task.Delay(20, Ct);
            }
        }
    }

    /// <summary>Logs one line per attempt, records the ambient correlation id, and fails the first attempts on request.</summary>
    internal sealed partial class LoggingJob(CorrelationProbe probe, ICorrelationContext correlation, ILogger<LoggingJob> logger)
        : IJob<CorrelationPayload>
    {
        public Task ExecuteAsync(CorrelationPayload payload, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(payload);
            LogRun(logger, payload.Marker);
            var attempt = probe.Record(payload.Marker, correlation.CorrelationId);
            return attempt <= payload.FailuresBeforeSuccess
                ? throw new InvalidOperationException("Correlation test job failure.")
                : Task.CompletedTask;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Correlation job ran for {Marker}")]
        private static partial void LogRun(ILogger logger, string marker);
    }

    /// <summary>Recurring job that logs and records the ambient correlation id under <see cref="Marker"/>.</summary>
    internal sealed partial class RecurringLoggingJob(CorrelationProbe probe, ICorrelationContext correlation, ILogger<LoggingJob> logger)
        : IRecurringJob
    {
        public const string Marker = "recurring-correlation";

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            LogRun(logger, Marker);
            probe.Record(Marker, correlation.CorrelationId);
            return Task.CompletedTask;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Correlation job ran for {Marker}")]
        private static partial void LogRun(ILogger logger, string marker);
    }
}
