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

/// <summary>The job contract on the in-memory queue, plus its specifics: fixed retries, never crashing the host, recurring replacement.</summary>
public sealed class InMemoryJobQueueContractTests : JobQueueContractTests
{
    private readonly RecordingLoggerProvider _logs = new();

    [Fact]
    public async Task FailingJob_AlwaysThrows_RunsFourTimesThenLogsErrorAndHostKeepsRunning()
    {
        var payload = NewPayload();
        var lifetime = TestHost.Services.GetRequiredService<IHostApplicationLifetime>();

        await Queue.EnqueueAsync<JobContractFailingJob, JobContractPayload>(payload, Ct);

        // First run plus 3 retries.
        await Probe.WaitForAsync(payload.Marker, 4, ExecutionTimeout, Ct);
        await WaitForAsync(() => _logs.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("no retries left", StringComparison.Ordinal)));
        await Task.Delay(200, Ct);

        Probe.Runs(payload.Marker).Count.ShouldBe(4);
        _logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("retrying", StringComparison.Ordinal)).ShouldBe(3);
        var failure = _logs.Entries.Single(e => e.Level == LogLevel.Error);
        failure.Exception.ShouldBeOfType<InvalidOperationException>();
        lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeFalse();

        var healthy = NewPayload();
        await Queue.EnqueueAsync<JobContractEchoJob, JobContractPayload>(healthy, Ct);
        (await Probe.WaitForAsync(healthy.Marker, 1, ExecutionTimeout, Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AddOrUpdateRecurringAsync_SameIdUpdatedToRareCron_StopsFrequentRuns()
    {
        const string id = "replace-me";
        await Queue.AddOrUpdateRecurringAsync<JobContractRecurringJob>(id, "* * * * * *", Ct);
        await Probe.WaitForAsync(JobContractRecurringJob.Marker, 1, RecurringTimeout, Ct);

        await Queue.AddOrUpdateRecurringAsync<JobContractRecurringJob>(id, "0 0 1 1 *", Ct);
        await Task.Delay(300, Ct);
        var countAfterUpdate = Probe.Runs(JobContractRecurringJob.Marker).Count;
        await Task.Delay(TimeSpan.FromSeconds(2.2), Ct);

        Probe.Runs(JobContractRecurringJob.Marker).Count.ShouldBe(countAfterUpdate);
    }

    [Fact]
    public async Task EnqueueAsync_NonSerializablePayload_ThrowsAtCallSite()
    {
        var payload = new SelfReferencing();
        payload.Self = payload;

        await Should.ThrowAsync<System.Text.Json.JsonException>(
            () => Queue.EnqueueAsync<SelfReferencingJob, SelfReferencing>(payload, Ct));
    }

    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("*/10 * * * * *")]
    [InlineData("0 3 * * MON-FRI")]
    public void ParseCron_FiveOrSixFields_Parses(string cron) =>
        InMemoryJobQueue.ParseCron(cron).ShouldNotBeNull();

    protected override void ConfigureProvider(IHostApplicationBuilder builder)
    {
        builder.Logging.AddProvider(_logs);
        new InMemoryJobsRegistration().Register(builder.Services, builder.Configuration);
        builder.Services.Configure<InMemoryJobQueueOptions>(o => o.RetryDelay = TimeSpan.FromMilliseconds(10));
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            elapsed.Elapsed.ShouldBeLessThan(ExecutionTimeout);
            await Task.Delay(20, Ct);
        }
    }

    private sealed class SelfReferencing
    {
        public SelfReferencing? Self { get; set; }
    }

    private sealed class SelfReferencingJob : IJob<SelfReferencing>
    {
        public Task ExecuteAsync(SelfReferencing payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
