using System.Net;
using Acme.Service.Host.Commands;
using Acme.Service.Host.Modules;
using Acme.Service.Testing;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.IntegrationTests;

public sealed class ReadinessTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetReady_PostgresReachableNoPendingMigrations_Returns200()
    {
        // The hosted modules own migrations, so "no pending migrations" means the database was migrated first (INV-16).
        var connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        (await MigrateCommand.RunAsync(
            ["migrate", $"--ConnectionStrings:PostgresMigrations={connectionString}", "--Jobs:Provider=InMemory"],
            HostModules.Create(),
            TextWriter.Null,
            TestContext.Current.CancellationToken)).ShouldBe(0);
        await using var factory = new ConfiguredWebFactory(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetReady_PendingModuleMigrations_Returns503()
    {
        var connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        await using var factory = new ConfiguredWebFactory(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetReady_PostgresUnreachable_Returns503AndLiveStays200()
    {
        await using var factory = new ConfiguredWebFactory(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Timeout=2",
        });
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed class ConfiguredWebFactory(IReadOnlyDictionary<string, string?> overrides) : ServiceWebFactory
    {
        protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides => overrides;
    }
}
