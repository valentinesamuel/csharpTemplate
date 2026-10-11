using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Web;
using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Infra.AzureBlob;
using Acme.Service.Infra.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing.Contracts;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Acme.Service.Infra.Tests.AzureBlob;

/// <summary>The shared <see cref="BlobStoreContractTests"/> plus Azure-specific behaviour, against Azurite.</summary>
[Trait("Category", "Integration")]
public sealed class AzureBlobStoreContractTests(AzuriteFixture azurite) : BlobStoreContractTests, IClassFixture<AzuriteFixture>
{
    private const string ContractContainer = "contract-tests";

    private readonly List<IDisposable> _owned = [];

    protected override bool SupportsDownloadUrls => true;

    protected override ValueTask<IBlobStore> CreateStoreAsync() =>
        ValueTask.FromResult<IBlobStore>(CreateStore(ContractContainer, createIfMissing: true));

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    [Fact]
    public async Task GetDownloadUrlAsync_ExistingBlob_UrlIsFetchableAndReturnsBytes()
    {
        var key = NewKey("download.bin");
        var content = RandomNumberGenerator.GetBytes(2048);
        await PutAsync(key, content);

        var url = await Store.GetDownloadUrlAsync(key, TimeSpan.FromMinutes(5), Ct);

        url.ShouldNotBeNull();
        var query = HttpUtility.ParseQueryString(url.Query);
        query["sp"].ShouldBe("r");
        query["sr"].ShouldBe("b");
        query["sig"].ShouldNotBeNullOrEmpty();
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(content);
    }

    [Fact]
    public async Task GetDownloadUrlAsync_ReadOnlySas_CannotWrite()
    {
        var key = NewKey("download.bin");
        await PutAsync(key, [1, 2, 3]);
        var url = await Store.GetDownloadUrlAsync(key, TimeSpan.FromMinutes(5), Ct);
        url.ShouldNotBeNull();

        using var http = new HttpClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent([9]) };
        put.Headers.Add("x-ms-blob-type", "BlockBlob");
        using var response = await http.SendAsync(put, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadAllAsync(key)).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(24 * 60)]
    public async Task GetDownloadUrlAsync_Lifetime_IsCappedAtFifteenMinutes(int minutes)
    {
        var before = DateTimeOffset.UtcNow;

        var url = await Store.GetDownloadUrlAsync(NewKey("file.bin"), TimeSpan.FromMinutes(minutes), Ct);

        url.ShouldNotBeNull();
        var expiresOn = DateTimeOffset.Parse(HttpUtility.ParseQueryString(url.Query)["se"]!, CultureInfo.InvariantCulture);
        var expected = TimeSpan.FromMinutes(Math.Min(minutes, 15));
        (expiresOn - before).ShouldBeLessThanOrEqualTo(expected + TimeSpan.FromSeconds(5));
        (expiresOn - before).ShouldBeGreaterThanOrEqualTo(expected - TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetDownloadUrlAsync_NonPositiveLifetime_ThrowsArgumentOutOfRange(int seconds) =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Store.GetDownloadUrlAsync(NewKey("file.bin"), TimeSpan.FromSeconds(seconds), Ct));

    [Theory]
    [InlineData(1024)]
    [InlineData(17 * 1024 * 1024)]
    public async Task PutAsync_NonSeekableStream_RoundTripsAndLeavesStreamOpen(int length)
    {
        var key = NewKey("stream.bin");
        var content = RandomNumberGenerator.GetBytes(length);
        await using var stream = new NonSeekableReadStream(content);

        await Store.PutAsync(key, stream, "application/octet-stream", Ct);

        stream.IsDisposed.ShouldBeFalse();
        (await ReadAllAsync(key)).ShouldBe(content);
    }

    [Fact]
    public async Task PutAsync_Key_IsStoredVerbatimWithContentType()
    {
        var key = NewKey("nested/dir/file.txt");
        using var stream = new MemoryStream([1]);

        await Store.PutAsync(key, stream, "text/plain", Ct);

        var container = new BlobServiceClient(azurite.ConnectionString).GetBlobContainerClient(ContractContainer);
        var properties = await container.GetBlobClient(key.Value).GetPropertiesAsync(cancellationToken: Ct);
        properties.Value.ContentType.ShouldBe("text/plain");
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, key.Value, Ct))
        {
            names.Add(item.Name);
        }

        names.ShouldHaveSingleItem().ShouldBe(key.Value);
    }

    [Fact]
    public async Task Operations_MissingContainerWithoutCreateIfMissing_ThrowNamingKeyAndDoNotCreate()
    {
        var name = UniqueContainer();
        var store = CreateStore(name, createIfMissing: false);
        var key = NewKey("file.bin");

        var put = await Should.ThrowAsync<InvalidOperationException>(() => store.PutAsync(key, new MemoryStream([1]), "application/octet-stream", Ct));
        put.Message.ShouldContain("Blob:AzureBlob:Container");
        put.Message.ShouldContain(name);
        put.Message.ShouldContain("Blob:AzureBlob:CreateIfMissing");
        (await Should.ThrowAsync<InvalidOperationException>(() => store.OpenReadAsync(key, Ct))).Message.ShouldContain("Blob:AzureBlob:Container");
        (await Should.ThrowAsync<InvalidOperationException>(() => store.ExistsAsync(key, Ct))).Message.ShouldContain("Blob:AzureBlob:Container");
        (await Should.ThrowAsync<InvalidOperationException>(() => store.DeleteAsync(key, Ct))).Message.ShouldContain("Blob:AzureBlob:Container");
        (await Should.ThrowAsync<InvalidOperationException>(() => store.GetDownloadUrlAsync(key, TimeSpan.FromMinutes(1), Ct)))
            .Message.ShouldContain("Blob:AzureBlob:Container");

        (await new BlobServiceClient(azurite.ConnectionString).GetBlobContainerClient(name).ExistsAsync(Ct)).Value.ShouldBeFalse();
    }

    [Fact]
    public async Task PutAsync_MissingContainerWithCreateIfMissing_CreatesContainerAndStores()
    {
        var name = UniqueContainer();
        var store = CreateStore(name, createIfMissing: true);
        var key = NewKey("file.bin");

        await store.PutAsync(key, new MemoryStream([7, 8]), "application/octet-stream", Ct);

        (await new BlobServiceClient(azurite.ConnectionString).GetBlobContainerClient(name).ExistsAsync(Ct)).Value.ShouldBeTrue();
        (await store.ExistsAsync(key, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Operations_ContainerDeletedAfterFirstUse_ThrowNamingKey()
    {
        var name = UniqueContainer();
        var container = new BlobServiceClient(azurite.ConnectionString).GetBlobContainerClient(name);
        await container.CreateAsync(cancellationToken: Ct);
        var store = CreateStore(name, createIfMissing: false);
        (await store.ExistsAsync(NewKey("file.bin"), Ct)).ShouldBeFalse();

        await container.DeleteAsync(cancellationToken: Ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => store.ExistsAsync(NewKey("file.bin"), Ct))).Message.ShouldContain("Blob:AzureBlob:Container");
        (await Should.ThrowAsync<InvalidOperationException>(() => store.OpenReadAsync(NewKey("file.bin"), Ct))).Message.ShouldContain("Blob:AzureBlob:Container");
    }

    private AzureBlobStore CreateStore(string container, bool createIfMissing)
    {
        var options = new AzureBlobOptions
        {
            ConnectionString = azurite.ConnectionString,
            Container = container,
            CreateIfMissing = createIfMissing,
        };
        var store = new AzureBlobStore(
            AzureBlobStore.CreateClient(options),
            Microsoft.Extensions.Options.Options.Create(options),
            TimeProvider.System,
            NullLogger<AzureBlobStore>.Instance);
        _owned.Add(store);
        return store;
    }

    private static string UniqueContainer() => "missing-" + Guid.NewGuid().ToString("N");
}
