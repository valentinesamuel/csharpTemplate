using Testcontainers.Azurite;
using Xunit;

namespace Acme.Service.Testing.Containers;

/// <summary>
/// Shared Azurite (Azure Storage emulator) container for integration tests (Docker required). Use as a class or
/// collection fixture. Tests share the account, so use unique blob or container names per test.
/// </summary>
/// <remarks>
/// The connection string carries Azurite's well-known development account key, the single allowlisted key (INV-11).
/// </remarks>
public sealed class AzuriteFixture : IAsyncLifetime
{
    /// <summary>Pinned Azurite image (recorded in state.md).</summary>
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:3.37.0";

    // WH-40 (confirmed): Azure.Storage.Blobs 12.30.1 sends API version 2026-10-06, which Azurite 3.37.0 rejects
    // unless the version check is skipped.
    private readonly AzuriteContainer _container = new AzuriteBuilder(Image)
        .WithInMemoryPersistence()
        .WithCommand("--skipApiVersionCheck")
        .Build();

    /// <summary>Connection string of the emulator account (shared key).</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await _container.StartAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
