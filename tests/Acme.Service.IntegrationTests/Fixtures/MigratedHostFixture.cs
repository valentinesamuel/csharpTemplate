using Acme.Service.Testing.Containers;

namespace Acme.Service.IntegrationTests.Fixtures;

/// <summary>
/// A class fixture: a Postgres container of its own, one database migrated with the real <c>migrate</c> verb, and one
/// <see cref="IntegrationWebFactory"/> over it. Tests sharing it use unique emails.
/// </summary>
public sealed class MigratedHostFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private IntegrationWebFactory? _factory;

    /// <summary>The Postgres container (for extra databases or hosts).</summary>
    internal PostgresFixture Postgres => _postgres;

    internal IntegrationWebFactory Factory => _factory ?? throw new InvalidOperationException("The fixture is not initialized.");

    internal ServiceApi Api => new(Factory);

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();
        var connectionString = await _postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        await ServiceApi.MigrateAsync(connectionString);
        _factory = new IntegrationWebFactory(connectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }
}
