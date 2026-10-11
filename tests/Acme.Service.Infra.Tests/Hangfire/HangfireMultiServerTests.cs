using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing.Contracts;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;

namespace Acme.Service.Infra.Tests.Hangfire;

/// <summary>Two Hangfire servers (replicas) on one database.</summary>
[Trait("Category", "Integration")]
public sealed class HangfireMultiServerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddOrUpdateRecurringAsync_TwoServersRegisterSameId_OneRecurringJobAndEachOccurrenceEnqueuedOnce()
    {
        var connectionString = await HangfireTestDatabase.CreateWithSchemaAsync(postgres, Ct);
        var probe = new JobContractProbe();
        using var first = await StartServerAsync(connectionString, probe);
        using var second = await StartServerAsync(connectionString, probe);
        var id = "replicas-" + Guid.NewGuid().ToString("N");

        // Both replicas register the job at startup, as modules do.
        await first.Services.GetRequiredService<IJobQueue>().AddOrUpdateRecurringAsync<JobContractRecurringJob>(id, "* * * * * *", Ct);
        await second.Services.GetRequiredService<IJobQueue>().AddOrUpdateRecurringAsync<JobContractRecurringJob>(id, "* * * * * *", Ct);
        await probe.WaitForAsync(JobContractRecurringJob.Marker, 4, TimeSpan.FromSeconds(30), Ct);
        await StopAsync(first, second);

        using (var connection = first.Services.GetRequiredService<JobStorage>().GetConnection())
        {
            connection.GetRecurringJobs().Count(r => r.Id == id).ShouldBe(1);
        }

        var occurrences = await OccurrencesAsync(connectionString, id);
        occurrences.Count.ShouldBeGreaterThanOrEqualTo(4);
        occurrences.ShouldAllBe(o => o.Jobs == 1, "an occurrence of the recurring job was enqueued more than once");
        probe.Runs(JobContractRecurringJob.Marker).Count.ShouldBeLessThanOrEqualTo((int)occurrences.Sum(o => o.Jobs));
    }

    [Fact]
    public async Task EnqueueAsync_TwoServers_EachJobRunsOnce()
    {
        var connectionString = await HangfireTestDatabase.CreateWithSchemaAsync(postgres, Ct);
        var probe = new JobContractProbe();
        using var first = await StartServerAsync(connectionString, probe);
        using var second = await StartServerAsync(connectionString, probe);
        var queue = first.Services.GetRequiredService<IJobQueue>();
        var payloads = Enumerable.Range(0, 20)
            .Select(i => new JobContractPayload(Guid.NewGuid().ToString("N"), i, []))
            .ToList();

        foreach (var payload in payloads)
        {
            await queue.EnqueueAsync<JobContractEchoJob, JobContractPayload>(payload, Ct);
        }

        foreach (var payload in payloads)
        {
            await probe.WaitForAsync(payload.Marker, 1, TimeSpan.FromSeconds(20), Ct);
        }

        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await StopAsync(first, second);
        payloads.ShouldAllBe(p => probe.Runs(p.Marker).Count == 1);
    }

    private static async Task<IHost> StartServerAsync(string connectionString, JobContractProbe probe)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Services.AddLogging();
        builder.Services.AddSingleton(probe);
        builder.Services.AddScoped<JobContractScopedDependency>();
        builder.Services.AddSingleton<ICorrelationContext, JobContractCorrelationContext>();
        HangfireTestDatabase.AddProvider(builder, connectionString);

        var host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    private static Task StopAsync(params IHost[] hosts) =>
        Task.WhenAll(hosts.Select(h => h.StopAsync(CancellationToken.None)));

    /// <summary>Background jobs created per occurrence of <paramref name="recurringJobId"/> (Hangfire's <c>Time</c> job parameter).</summary>
    private static async Task<IReadOnlyList<(string Time, long Jobs)>> OccurrencesAsync(string connectionString, string recurringJobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT t.value, count(*)
            FROM hangfire.jobparameter r
            JOIN hangfire.jobparameter t ON t.jobid = r.jobid AND t.name = 'Time'
            WHERE r.name = 'RecurringJobId' AND r.value = @id
            GROUP BY t.value
            """,
            connection);
        command.Parameters.AddWithValue("id", $"\"{recurringJobId}\"");

        var occurrences = new List<(string, long)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            occurrences.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return occurrences;
    }
}
