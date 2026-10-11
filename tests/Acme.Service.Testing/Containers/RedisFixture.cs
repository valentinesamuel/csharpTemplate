using Testcontainers.Redis;
using Xunit;

namespace Acme.Service.Testing.Containers;

/// <summary>
/// Shared Redis container for integration tests (Docker required). Use as a class or collection fixture. Tests
/// share the server, so use unique keys per test.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    /// <summary>Pinned Redis image (recorded in state.md).</summary>
    public const string Image = "redis:8.8.3-alpine";

    private readonly RedisContainer _container = new RedisBuilder(Image).Build();

    /// <summary>StackExchange.Redis configuration string of the container (<c>host:port</c>).</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// Stops the server (an outage). Destructive for every user of this container, so use a container of the test's
    /// own. A stopped container is not restarted: its mapped port may change.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => _container.StopAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await _container.StartAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
