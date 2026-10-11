using System.Text.Json;
using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Infra.Hangfire;
using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.Infra.Tests.Fixtures;
using Acme.Service.Testing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Acme.Service.Infra.Tests.Hangfire;

/// <summary>
/// The Hangfire runners give every run a correlation id (LOG-09, D-07 A): the enqueue-time id from
/// <see cref="HangfireJobMetadata"/> unchanged, otherwise a generated 32-hex id per attempt; the ambient
/// <see cref="ICorrelationContext"/> is restored afterwards. Runs the runners directly: no Hangfire server, no database.
/// </summary>
public sealed partial class HangfireJobCorrelationTests : IDisposable
{
    private readonly ScopeRecordingLoggerProvider _logs = new();
    private readonly ServiceProvider _services;

    public HangfireJobCorrelationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(_logs));
        services.AddSingleton<ICorrelationContext, JobContractCorrelationContext>();
        services.AddSingleton<SeenCorrelation>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ICorrelationContext Correlation => _services.GetRequiredService<ICorrelationContext>();

    private SeenCorrelation Seen => _services.GetRequiredService<SeenCorrelation>();

    [Fact]
    public async Task JobRunner_NullMetadata_RunLogsAndSeesGeneratedId()
    {
        await JobRunner().RunAsync(Payload(), metadata: null, Ct);

        var id = Seen.CorrelationId.ShouldNotBeNull();
        id.ShouldMatch("^[a-f0-9]{32}$");
        JobLine().CorrelationId.ShouldBe(id);
        CompletedLine<LoggingJob>().CorrelationId.ShouldBe(id);
    }

    [Fact]
    public async Task JobRunner_MetadataWithoutId_RunLogsAndSeesGeneratedId()
    {
        await JobRunner().RunAsync(Payload(), new HangfireJobMetadata(CorrelationId: null), Ct);

        var id = Seen.CorrelationId.ShouldNotBeNull();
        id.ShouldMatch("^[a-f0-9]{32}$");
        JobLine().CorrelationId.ShouldBe(id);
    }

    [Fact]
    public async Task JobRunner_MetadataWithId_RunLogsAndSeesItUnchanged()
    {
        await JobRunner().RunAsync(Payload(), new HangfireJobMetadata("abc-123"), Ct);

        Seen.CorrelationId.ShouldBe("abc-123");
        JobLine().CorrelationId.ShouldBe("abc-123");
        CompletedLine<LoggingJob>().CorrelationId.ShouldBe("abc-123");
    }

    [Fact]
    public async Task RecurringRunner_Occurrence_RunLogsAndSeesGeneratedId()
    {
        var runner = new HangfireRecurringJobRunner<RecurringLoggingJob>(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<ILogger<HangfireRecurringJobRunner<RecurringLoggingJob>>>());

        await runner.RunAsync(Ct);

        var id = Seen.CorrelationId.ShouldNotBeNull();
        id.ShouldMatch("^[a-f0-9]{32}$");
        JobLine().CorrelationId.ShouldBe(id);
        CompletedLine<RecurringLoggingJob>().CorrelationId.ShouldBe(id);
    }

    [Fact]
    public async Task JobRunner_RunsTwiceWithoutId_EachAttemptGetsItsOwnId()
    {
        await JobRunner().RunAsync(Payload(), metadata: null, Ct);
        var first = Seen.CorrelationId;
        await JobRunner().RunAsync(Payload(), metadata: null, Ct);

        first.ShouldNotBeNull().ShouldMatch("^[a-f0-9]{32}$");
        Seen.CorrelationId.ShouldNotBeNull().ShouldMatch("^[a-f0-9]{32}$");
        Seen.CorrelationId.ShouldNotBe(first);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc-123")]
    public async Task JobRunner_AfterRun_AmbientCorrelationIdIsRestored(string? enqueued)
    {
        Correlation.CorrelationId = "ambient-before";
        try
        {
            await JobRunner().RunAsync(Payload(), new HangfireJobMetadata(enqueued), Ct);

            Seen.CorrelationId.ShouldNotBe("ambient-before");
            Correlation.CorrelationId.ShouldBe("ambient-before");
        }
        finally
        {
            Correlation.CorrelationId = null;
        }
    }

    [Fact]
    public async Task JobRunner_AfterRunWithNoAmbientId_AmbientStaysNull()
    {
        await JobRunner().RunAsync(Payload(), new HangfireJobMetadata("abc-123"), Ct);

        Seen.CorrelationId.ShouldBe("abc-123");
        Correlation.CorrelationId.ShouldBeNull();
    }

    [Fact]
    public async Task JobRunner_JobThrows_AmbientCorrelationIdIsRestored()
    {
        var runner = new HangfireJobRunner<FailingJob, CorrelationPayload>(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<ILogger<HangfireJobRunner<FailingJob, CorrelationPayload>>>());
        Correlation.CorrelationId = "ambient-before";
        try
        {
            await Should.ThrowAsync<InvalidOperationException>(() => runner.RunAsync(Payload(), metadata: null, Ct));

            Correlation.CorrelationId.ShouldBe("ambient-before");
        }
        finally
        {
            Correlation.CorrelationId = null;
        }
    }

    public void Dispose() => _services.Dispose();

    private static string Payload() => JsonSerializer.Serialize(new CorrelationPayload("hangfire-correlation"));

    private HangfireJobRunner<LoggingJob, CorrelationPayload> JobRunner() =>
        new(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<ILogger<HangfireJobRunner<LoggingJob, CorrelationPayload>>>());

    private ScopedLogEntry JobLine() =>
        _logs.Entries.Last(e => e.Message.StartsWith("Correlation job ran for ", StringComparison.Ordinal));

    private ScopedLogEntry CompletedLine<TJob>() =>
        _logs.Entries.Last(e => e.Message == $"Job {typeof(TJob).Name} completed");

    /// <summary>Payload of the test jobs.</summary>
    internal sealed record CorrelationPayload(string Marker);

    /// <summary>The ambient correlation id the last run saw.</summary>
    internal sealed class SeenCorrelation
    {
        public string? CorrelationId { get; set; }
    }

    /// <summary>Logs one line and records the ambient correlation id.</summary>
    internal sealed partial class LoggingJob(SeenCorrelation seen, ICorrelationContext correlation, ILogger<LoggingJob> logger)
        : IJob<CorrelationPayload>
    {
        public Task ExecuteAsync(CorrelationPayload payload, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(payload);
            seen.CorrelationId = correlation.CorrelationId;
            LogRun(logger, payload.Marker);
            return Task.CompletedTask;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Correlation job ran for {Marker}")]
        internal static partial void LogRun(ILogger logger, string marker);
    }

    /// <summary>Recurring variant of <see cref="LoggingJob"/> (logs under the same category).</summary>
    internal sealed class RecurringLoggingJob(SeenCorrelation seen, ICorrelationContext correlation, ILogger<LoggingJob> logger)
        : IRecurringJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            seen.CorrelationId = correlation.CorrelationId;
            LoggingJob.LogRun(logger, "recurring");
            return Task.CompletedTask;
        }
    }

    /// <summary>Always fails.</summary>
    internal sealed class FailingJob : IJob<CorrelationPayload>
    {
        public Task ExecuteAsync(CorrelationPayload payload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Correlation test job failure.");
    }
}
