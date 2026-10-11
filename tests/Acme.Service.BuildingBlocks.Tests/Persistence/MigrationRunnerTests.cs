using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Persistence;

[Collection(PostgresTestGroup.Name)]
public sealed class MigrationRunnerTests(PostgresFixture postgres)
{
    private const string InitialMigration = "InitialCreate";
    private const string MessagingMigration = "AddMessaging";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListPendingAsync_FreshDatabase_ListsAllMigrationsInOrder()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await using var services = WidgetsHost.BuildServices(connectionString, migrationRunner: true);

        var pending = await services.GetRequiredService<MigrationRunner>().ListPendingAsync(Ct);

        var widgets = pending.ShouldHaveSingleItem();
        widgets.ModuleKey.ShouldBe(WidgetsDbContext.ModuleKey);
        widgets.ContextName.ShouldBe(nameof(WidgetsDbContext));
        widgets.Migrations.Count.ShouldBe(2);
        widgets.Migrations[0].ShouldEndWith(InitialMigration);
        widgets.Migrations[1].ShouldEndWith(MessagingMigration);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_FreshDatabase_AppliesMigrationsWithHistoryInModuleSchema()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await using var services = WidgetsHost.BuildServices(connectionString, migrationRunner: true);
        var runner = services.GetRequiredService<MigrationRunner>();

        await runner.RunAsync(Ct);

        (await runner.ListPendingAsync(Ct)).ShouldHaveSingleItem().Migrations.ShouldBeEmpty();
        (await TablesInSchemaAsync(connectionString, "widgets")).ShouldBe(["__ef_migrations_history", "inbox_messages", "outbox_messages", "widgets"]);
        (await TablesInSchemaAsync(connectionString, "public")).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_RunTwice_IsIdempotent()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await using var services = WidgetsHost.BuildServices(connectionString, migrationRunner: true);
        var runner = services.GetRequiredService<MigrationRunner>();

        await runner.RunAsync(Ct);
        await runner.RunAsync(Ct);

        (await runner.ListPendingAsync(Ct)).ShouldHaveSingleItem().Migrations.ShouldBeEmpty();
        (await AppliedMigrationCountAsync(connectionString)).ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_ConcurrentRunsOnFreshDatabase_AllSucceedAndApplyOnce()
    {
        // WH-08: two (here four) migrate jobs started at once must not fail or double-apply.
        for (var round = 0; round < 3; round++)
        {
            var connectionString = await postgres.CreateDatabaseAsync(Ct);
            var providers = Enumerable.Range(0, 4)
                .Select(_ => WidgetsHost.BuildServices(connectionString, migrationRunner: true))
                .ToList();
            try
            {
                await Task.WhenAll(providers.Select(p => p.GetRequiredService<MigrationRunner>().RunAsync(Ct)));
            }
            finally
            {
                foreach (var provider in providers)
                {
                    await provider.DisposeAsync();
                }
            }

            (await AppliedMigrationCountAsync(connectionString)).ShouldBe(2);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_PostMigrationSteps_RunAfterContextsInOrder()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        var log = new StepLog();
        await using var services = WidgetsHost.BuildServices(
            connectionString,
            migrationRunner: true,
            services => services
                .AddSingleton(log)
                .AddSingleton(new ConnectionProbe(connectionString))
                .AddPostMigrationStep<LateStep>()
                .AddPostMigrationStep<EarlyStep>());

        await services.GetRequiredService<MigrationRunner>().RunAsync(Ct);

        log.Entries.ShouldBe(["early:migrated", "late:migrated"]);
    }

    private static async Task<List<string>> TablesInSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema ORDER BY table_name", connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<long> AppliedMigrationCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM widgets.__ef_migrations_history", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    internal sealed class StepLog
    {
        public List<string> Entries { get; } = [];
    }

    internal sealed record ConnectionProbe(string ConnectionString)
    {
        public async Task<string> StateAsync(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT to_regclass('widgets.widgets') IS NOT NULL", connection);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken))! ? "migrated" : "not-migrated";
        }
    }

    internal sealed class EarlyStep(StepLog log, ConnectionProbe probe) : IPostMigrationStep
    {
        public string Name => "early";

        public int Order => 10;

        public async Task ExecuteAsync(CancellationToken cancellationToken) =>
            log.Entries.Add($"{Name}:{await probe.StateAsync(cancellationToken)}");
    }

    internal sealed class LateStep(StepLog log, ConnectionProbe probe) : IPostMigrationStep
    {
        public string Name => "late";

        public int Order => 20;

        public async Task ExecuteAsync(CancellationToken cancellationToken) =>
            log.Entries.Add($"{Name}:{await probe.StateAsync(cancellationToken)}");
    }
}
