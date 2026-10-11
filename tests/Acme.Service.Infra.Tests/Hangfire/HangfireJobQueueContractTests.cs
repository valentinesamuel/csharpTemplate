using Acme.Service.BuildingBlocks.Infra.Hangfire;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing.Contracts;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Acme.Service.Infra.Tests.Hangfire;

/// <summary>
/// The job contract on Hangfire with Postgres storage (enqueue, schedule after a delay, recurring, correlation id, failing
/// jobs), each test on a fresh database whose schema was installed by the migrate step.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HangfireJobQueueContractTests(PostgresFixture postgres) : JobQueueContractTests, IClassFixture<PostgresFixture>
{
    private string? _connectionString;

    protected override TimeSpan ExecutionTimeout => TimeSpan.FromSeconds(20);

    protected override TimeSpan RecurringTimeout => TimeSpan.FromSeconds(30);

    public override async ValueTask InitializeAsync()
    {
        _connectionString = await HangfireTestDatabase.CreateWithSchemaAsync(postgres, Ct);
        await base.InitializeAsync();
    }

    [Fact]
    public async Task EnqueueAsync_Job_IsPersistedAsHangfireJob()
    {
        var id = await Queue.EnqueueAsync<JobContractEchoJob, JobContractPayload>(NewPayload(), Ct);

        using var connection = TestHost.Services.GetRequiredService<JobStorage>().GetConnection();
        var job = connection.GetJobData(id).ShouldNotBeNull();
        job.LoadException.ShouldBeNull(job.LoadException?.ToString());
        job.Job.Type.ShouldBe(typeof(HangfireJobRunner<JobContractEchoJob, JobContractPayload>));
        job.Job.Args[0].ShouldBeOfType<string>().ShouldContain("\"Marker\"");
    }

    [Fact]
    public async Task ScheduleAsync_Delay_IsDueNoEarlierThanDelayRoundedUpToSecond()
    {
        var before = DateTimeOffset.UtcNow;

        var id = await Queue.ScheduleAsync<JobContractEchoJob, JobContractPayload>(NewPayload(), TimeSpan.FromMinutes(5), Ct);

        using var connection = TestHost.Services.GetRequiredService<JobStorage>().GetConnection();
        var state = connection.GetStateData(id).ShouldNotBeNull();
        state.Name.ShouldBe("Scheduled");
        var enqueueAt = new DateTimeOffset(JobHelper.DeserializeDateTime(state.Data["EnqueueAt"]), TimeSpan.Zero);
        enqueueAt.ShouldBeGreaterThanOrEqualTo(before + TimeSpan.FromMinutes(5));
        enqueueAt.Millisecond.ShouldBe(0);
    }

    [Fact]
    public async Task AddOrUpdateRecurringAsync_SameIdTwice_StoresOneRecurringJobWithLatestCron()
    {
        var id = "contract-" + Guid.NewGuid().ToString("N");

        await Queue.AddOrUpdateRecurringAsync<JobContractRecurringJob>(id, "0 0 1 1 *", Ct);
        await Queue.AddOrUpdateRecurringAsync<JobContractRecurringJob>(id, "0 3 * * *", Ct);

        using var connection = TestHost.Services.GetRequiredService<JobStorage>().GetConnection();
        var recurring = connection.GetRecurringJobs([id]).ShouldHaveSingleItem();
        recurring.Cron.ShouldBe("0 3 * * *");
        recurring.TimeZoneId.ShouldBe(TimeZoneInfo.Utc.Id);
    }

    protected override void ConfigureProvider(IHostApplicationBuilder builder) =>
        HangfireTestDatabase.AddProvider(builder, _connectionString ?? throw new InvalidOperationException("No database."));
}
