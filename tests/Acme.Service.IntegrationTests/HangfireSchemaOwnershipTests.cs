using System.Net;
using Acme.Service.BuildingBlocks.Infra.Hangfire;
using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.Host.Commands;
using Acme.Service.Host.Modules;
using Acme.Service.Testing;
using Acme.Service.Testing.Containers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace Acme.Service.IntegrationTests;

/// <summary>INV-16 for Hangfire: the <c>migrate</c> verb installs the <c>hangfire</c> schema; the web app never does.</summary>
public sealed class HangfireSchemaOwnershipTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrate_JobsProviderHangfire_InstallsHangfireSchemaIdempotently()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        string[] args = ["migrate", $"--ConnectionStrings:PostgresMigrations={connectionString}", "--Jobs:Provider=Hangfire"];

        var first = await MigrateCommand.RunAsync(args, [], TextWriter.Null, Ct);
        var tables = await HangfireTableCountAsync(connectionString);
        var second = await MigrateCommand.RunAsync(args, [], TextWriter.Null, Ct);

        first.ShouldBe(0);
        second.ShouldBe(0);
        (await SchemaExistsAsync(connectionString, "hangfire")).ShouldBeTrue();
        tables.ShouldBeGreaterThan(0);
        (await HangfireTableCountAsync(connectionString)).ShouldBe(tables);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrate_JobsProviderInMemory_DoesNotCreateHangfireSchema()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);

        var exitCode = await MigrateCommand.RunAsync(
            ["migrate", $"--ConnectionStrings:PostgresMigrations={connectionString}", "--Jobs:Provider=InMemory"],
            [],
            TextWriter.Null,
            Ct);

        exitCode.ShouldBe(0);
        (await SchemaExistsAsync(connectionString, "hangfire")).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrate_JobsProviderHangfireWithoutMigrationConnectionString_FailsNamingKey()
    {
        var exception = await Should.ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>(
            () => MigrateCommand.RunAsync(["migrate", "--Jobs:Provider=Hangfire", "--ConnectionStrings:PostgresMigrations="], [], TextWriter.Null, Ct));

        exception.Message.ShouldContain("ConnectionStrings:PostgresMigrations is required to install the Hangfire schema.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WebApp_JobsProviderHangfireOnFreshDatabase_StartsReadyAndNeverCreatesSchema()
    {
        // Module migrations applied (so readiness can pass) but the Hangfire schema deliberately not installed.
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        (await MigrateCommand.RunAsync(
            ["migrate", $"--ConnectionStrings:PostgresMigrations={connectionString}", "--Jobs:Provider=InMemory"],
            HostModules.Create(),
            TextWriter.Null,
            Ct)).ShouldBe(0);
        await using var factory = new ConfiguredWebFactory(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["Jobs:Provider"] = "Hangfire",
            ["Jobs:Hangfire:DashboardEnabled"] = "true",
        });
        using var client = factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        using var dashboard = await client.GetAsync(new Uri("/hangfire", UriKind.Relative), Ct);

        // Give the Hangfire server time to start its workers and schedulers against the missing schema.
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        // The dashboard is not mapped outside Development (D-21); the unmatched request is rejected by the
        // fail-closed fallback policy (401, WH-16) rather than served.
        dashboard.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .ShouldNotContain(e => (e.RoutePattern.RawText ?? string.Empty).StartsWith("/hangfire", StringComparison.Ordinal));
        factory.Services.GetRequiredService<IJobQueue>().GetType().Assembly.ShouldBe(typeof(HangfireRegistration).Assembly);
        (await SchemaExistsAsync(connectionString, "hangfire")).ShouldBeFalse();
    }

    private static async Task<bool> SchemaExistsAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema)", connection);
        command.Parameters.AddWithValue("schema", schema);
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<long> HangfireTableCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'hangfire'", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class ConfiguredWebFactory(IReadOnlyDictionary<string, string?> overrides) : ServiceWebFactory
    {
        protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides => overrides;
    }
}
