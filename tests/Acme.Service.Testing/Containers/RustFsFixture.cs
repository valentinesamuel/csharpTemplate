using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Acme.Service.Testing.Containers;

/// <summary>
/// Shared S3-compatible object store for integration tests (Docker required). Use as a class or collection fixture.
/// Tests share the server, so use unique keys or bucket names per test.
/// </summary>
/// <remarks>
/// The plan names MinIO, but MinIO server images are no longer pullable from Docker Hub or quay.io (WH-41), so this
/// runs RustFS (Apache-2.0, MinIO-compatible S3 API with SigV4 and presigned URLs). Path-style addressing is required.
/// The credentials are throw-away values for the disposable container only.
/// </remarks>
public sealed class RustFsFixture : IAsyncLifetime
{
    /// <summary>Pinned RustFS image (recorded in state.md).</summary>
    public const string Image = "rustfs/rustfs:1.0.1";

    /// <summary>Access key of the container's root user.</summary>
    public const string AccessKey = "rustfs-test";

    /// <summary>Secret key of the container's root user (test container only).</summary>
    public const string SecretKey = "rustfs-test-secret";

    /// <summary>Signing region used by the tests.</summary>
    public const string Region = "us-east-1";

    private const ushort S3Port = 9000;

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(S3Port, assignRandomHostPort: true)
        .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/health/ready")))
        .Build();

    /// <summary>S3 endpoint of the container (<c>http://host:port</c>).</summary>
    public string ServiceUrl => string.Create(
        CultureInfo.InvariantCulture,
        $"http://{_container.Hostname}:{_container.GetMappedPublicPort(S3Port)}");

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await _container.StartAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
