using System.Security.Cryptography;
using Acme.Service.BuildingBlocks.Blob;
using Shouldly;
using Xunit;

namespace Acme.Service.Testing.Contracts;

/// <summary>
/// Behaviour every <see cref="IBlobStore"/> provider must have (P7; reused by S3 and Azure Blob in P9). Derive,
/// implement <see cref="CreateStoreAsync"/> and <see cref="SupportsDownloadUrls"/>, and add the provider's own traits.
/// Keys are unique per test, so a shared bucket or container is fine. Provider suites add a check that a returned
/// download URL is actually fetchable.
/// </summary>
public abstract class BlobStoreContractTests : IAsyncLifetime
{
    private const string ContentType = "application/octet-stream";

    private IBlobStore? _store;

    /// <summary>The store under test (created per test).</summary>
    protected IBlobStore Store => _store ?? throw new InvalidOperationException("The store is not initialized.");

    /// <summary>True when the provider issues download URLs; false when <see cref="IBlobStore.GetDownloadUrlAsync"/> returns null.</summary>
    protected abstract bool SupportsDownloadUrls { get; }

    /// <summary>Creates the store under test.</summary>
    protected abstract ValueTask<IBlobStore> CreateStoreAsync();

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync() => _store = await CreateStoreAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        switch (_store)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task PutAsync_ThenOpenRead_ReturnsSameBytes()
    {
        var key = NewKey("file.bin");
        var content = RandomBytes(4096);

        await PutAsync(key, content);

        (await ReadAllAsync(key)).ShouldBe(content);
    }

    [Fact]
    public async Task PutAsync_LargeContent_RoundTrips()
    {
        var key = NewKey("large.bin");
        var content = RandomBytes(5 * 1024 * 1024);

        await PutAsync(key, content);

        (await ReadAllAsync(key)).ShouldBe(content);
    }

    [Fact]
    public async Task PutAsync_EmptyContent_RoundTrips()
    {
        var key = NewKey("empty.bin");

        await PutAsync(key, []);

        (await Store.ExistsAsync(key, Ct)).ShouldBeTrue();
        (await ReadAllAsync(key)).ShouldBeEmpty();
    }

    [Fact]
    public async Task PutAsync_ExistingKey_Overwrites()
    {
        var key = NewKey("file.bin");
        await PutAsync(key, RandomBytes(100));
        var second = RandomBytes(50);

        await PutAsync(key, second);

        (await ReadAllAsync(key)).ShouldBe(second);
    }

    [Fact]
    public async Task PutAsync_NestedKeys_AreIndependent()
    {
        var parent = NewKey("a");
        var first = BlobKey.Create(parent.Value + "/one.txt");
        var second = BlobKey.Create(parent.Value + "/nested/two.txt");

        await PutAsync(first, [1]);
        await PutAsync(second, [2]);

        (await ReadAllAsync(first)).ShouldBe(new byte[] { 1 });
        (await ReadAllAsync(second)).ShouldBe(new byte[] { 2 });
    }

    [Fact]
    public async Task ExistsAsync_PutThenDeleted_ReflectsState()
    {
        var key = NewKey("file.bin");

        (await Store.ExistsAsync(key, Ct)).ShouldBeFalse();
        await PutAsync(key, [1, 2, 3]);
        (await Store.ExistsAsync(key, Ct)).ShouldBeTrue();

        await Store.DeleteAsync(key, Ct);

        (await Store.ExistsAsync(key, Ct)).ShouldBeFalse();
        (await Store.OpenReadAsync(key, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task OpenReadAsync_MissingKey_ReturnsNull() =>
        (await Store.OpenReadAsync(NewKey("missing.bin"), Ct)).ShouldBeNull();

    [Fact]
    public async Task DeleteAsync_MissingKey_DoesNotThrow() =>
        await Should.NotThrowAsync(() => Store.DeleteAsync(NewKey("missing.bin"), Ct));

    [Fact]
    public async Task GetDownloadUrlAsync_ExistingBlob_MatchesProviderSupport()
    {
        var key = NewKey("file.bin");
        await PutAsync(key, [1, 2, 3]);

        var url = await Store.GetDownloadUrlAsync(key, TimeSpan.FromMinutes(5), Ct);

        if (SupportsDownloadUrls)
        {
            url.ShouldNotBeNull();
            url.IsAbsoluteUri.ShouldBeTrue();
        }
        else
        {
            url.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Operations_NullArguments_ThrowArgumentNullException()
    {
        var key = NewKey("file.bin");

        await Should.ThrowAsync<ArgumentNullException>(() => Store.PutAsync(null!, Stream.Null, ContentType, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => Store.PutAsync(key, null!, ContentType, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => Store.OpenReadAsync(null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => Store.ExistsAsync(null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => Store.DeleteAsync(null!, Ct));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../b")]
    [InlineData("a\\..\\b")]
    [InlineData("%2e%2e%2fetc%2fpasswd")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a\0b")]
    public void BlobKey_TraversalCandidate_IsRejected(string candidate) =>
        Should.Throw<ArgumentException>(() => BlobKey.Create(candidate));

    /// <summary>The current test's cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A key under a prefix unique to this call.</summary>
    protected static BlobKey NewKey(string name) => BlobKey.Create($"contract/{Guid.NewGuid():N}/{name}");

    /// <summary>Writes <paramref name="content"/> under <paramref name="key"/>.</summary>
    protected async Task PutAsync(BlobKey key, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        await Store.PutAsync(key, stream, ContentType, Ct).ConfigureAwait(false);
    }

    /// <summary>Reads the whole blob, failing when it is missing.</summary>
    protected async Task<byte[]> ReadAllAsync(BlobKey key)
    {
        var stream = await Store.OpenReadAsync(key, Ct).ConfigureAwait(false);
        stream.ShouldNotBeNull();
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, Ct).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    private static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);
}
