using System.Globalization;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Acme.Service.Testing.Containers;

/// <summary>
/// Shared Postgres container for integration tests (Docker required). Use as a class or collection fixture;
/// call <see cref="CreateDatabaseAsync"/> for an isolated, empty database per test.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Pinned Postgres image (recorded in state.md).</summary>
    public const string Image = "postgres:18.6-alpine";

    /// <summary>
    /// Server connection limit. Every test gets its own database, and each database has its own client pools whose idle
    /// connections outlive the test for a while, so the default of 100 is too low for a whole test project.
    /// </summary>
    public const int MaxConnections = 500;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithCommand("-c", string.Create(CultureInfo.InvariantCulture, $"max_connections={MaxConnections}"))
        .Build();

    /// <summary>Connection string of the container's default database (superuser).</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = "test_" + Guid.NewGuid().ToString("N");

        var connection = new NpgsqlConnection(ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // The name is generated above (hex only), never user input; identifiers cannot be parameters.
            var command = new NpgsqlCommand(string.Create(CultureInfo.InvariantCulture, $"CREATE DATABASE \"{name}\""), connection);
            await using (command.ConfigureAwait(false))
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>
    /// Terminates every server connection to the database of <paramref name="connectionString"/>, simulating a
    /// failover: pooled client connections break and their next use fails with a transient error.
    /// </summary>
    public async Task TerminateConnectionsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;

        var connection = new NpgsqlConnection(ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = new NpgsqlCommand(
                "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid()",
                connection);
            await using (command.ConfigureAwait(false))
            {
                command.Parameters.AddWithValue("database", database ?? string.Empty);
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Freezes the server (<c>docker pause</c>): connections stay open but nothing answers, as in a network partition or a
    /// stalled failover. Destructive for every user of this container, so use a container of the test's own.
    /// </summary>
    public Task PauseAsync(CancellationToken cancellationToken) => _container.PauseAsync(cancellationToken);

    /// <summary>Resumes a server frozen by <see cref="PauseAsync"/>.</summary>
    public Task UnpauseAsync(CancellationToken cancellationToken) => _container.UnpauseAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await _container.StartAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
