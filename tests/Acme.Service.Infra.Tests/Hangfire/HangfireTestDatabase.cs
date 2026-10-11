using Acme.Service.BuildingBlocks.Infra.Hangfire;
using Acme.Service.Testing.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Acme.Service.Infra.Tests.Hangfire;

/// <summary>Test helpers: a fresh database (optionally with the Hangfire schema) and the provider with fast polling.</summary>
internal static class HangfireTestDatabase
{
    /// <summary>Queue and scheduler polling used by the tests (production default: 15 s).</summary>
    public static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Creates an empty database and installs the Hangfire schema with the migrate step.</summary>
    public static async Task<string> CreateWithSchemaAsync(PostgresFixture postgres, CancellationToken cancellationToken)
    {
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
        await InstallSchemaAsync(connectionString, HangfireOptions.DefaultSchema, cancellationToken);
        return connectionString;
    }

    /// <summary>Runs <see cref="HangfireSchemaPostMigrationStep"/> against <paramref name="connectionString"/>.</summary>
    public static Task InstallSchemaAsync(string connectionString, string schema, CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgresMigrations"] = connectionString,
                ["Jobs:Hangfire:Schema"] = schema,
            })
            .Build();
        return new HangfireSchemaPostMigrationStep(configuration, NullLogger<HangfireSchemaPostMigrationStep>.Instance)
            .ExecuteAsync(cancellationToken);
    }

    /// <summary>Registers the Hangfire provider on <paramref name="connectionString"/> with fast polling and few workers.</summary>
    public static void AddProvider(IHostApplicationBuilder builder, string connectionString)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["Jobs:Hangfire:WorkerCount"] = "4",
        });
        new HangfireRegistration().Register(builder.Services, builder.Configuration);
        builder.Services.Configure<HangfireOptions>(o =>
        {
            o.QueuePollInterval = PollingInterval;
            o.SchedulePollingInterval = PollingInterval;
        });
    }

    /// <summary>True when <paramref name="schema"/> exists in the database of <paramref name="connectionString"/>.</summary>
    public static async Task<bool> SchemaExistsAsync(string connectionString, string schema, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken);
            var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema)", connection);
            await using (command.ConfigureAwait(false))
            {
                command.Parameters.AddWithValue("schema", schema);
                return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
            }
        }
    }

    /// <summary>The installed Hangfire schema version (<c>&lt;schema&gt;.schema</c>).</summary>
    public static async Task<int> SchemaVersionAsync(string connectionString, string schema, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken);

            // The schema name comes from the tests (validated identifier), never from user input.
            var command = new NpgsqlCommand($"SELECT max(version) FROM \"{schema}\".\"schema\"", connection);
            await using (command.ConfigureAwait(false))
            {
                return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
            }
        }
    }
}
