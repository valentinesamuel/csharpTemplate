using Acme.Service.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Acme.Service.IntegrationTests.Fixtures;

/// <summary>
/// A Postgres container owned by ONE test (pausing it must not disturb other tests), a migrated database, and the web
/// host over it with short Npgsql timeouts (<see cref="ServiceApi.WithShortTimeouts"/>). Disposal unpauses the container
/// if the test left it paused, then removes host and container.
/// </summary>
internal sealed class PausablePostgresHost : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private bool _paused;

    private PausablePostgresHost(PostgresFixture postgres, IntegrationWebFactory factory)
    {
        _postgres = postgres;
        Factory = factory;
    }

    public IntegrationWebFactory Factory { get; }

    public ServiceApi Api => new(Factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<PausablePostgresHost> StartAsync(
        IReadOnlyDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var postgres = new PostgresFixture();
        try
        {
            await postgres.InitializeAsync();
            var connectionString = await postgres.CreateDatabaseAsync(Ct);
            await ServiceApi.MigrateAsync(connectionString);
            var factory = new IntegrationWebFactory(ServiceApi.WithShortTimeouts(connectionString), overrides, configureServices);
            return new PausablePostgresHost(postgres, factory);
        }
        catch
        {
            await postgres.DisposeAsync();
            throw;
        }
    }

    public async Task PauseAsync()
    {
        await _postgres.PauseAsync(Ct);
        _paused = true;
    }

    /// <summary>Unpauses the container and waits until the test database answers again.</summary>
    public async Task UnpauseAsync()
    {
        await _postgres.UnpauseAsync(Ct);
        _paused = false;
        await ServiceApi.EventuallyAsync(
            async () =>
            {
                try
                {
                    return await ServiceApi.ScalarAsync<int>(Factory.ConnectionString, "SELECT 1") == 1;
                }
                catch (NpgsqlException)
                {
                    return false;
                }
            },
            "the database answers after the unpause",
            TimeSpan.FromSeconds(30));
    }

    public async ValueTask DisposeAsync()
    {
        if (_paused)
        {
            await _postgres.UnpauseAsync(CancellationToken.None);
        }

        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
