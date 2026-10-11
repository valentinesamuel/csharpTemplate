using System.Net;
using System.Security.Cryptography;
using System.Web;
using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Infra.S3;
using Acme.Service.Infra.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing.Contracts;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Acme.Service.Infra.Tests.S3;

/// <summary>The shared <see cref="BlobStoreContractTests"/> plus S3-specific behaviour, against RustFS (WH-41).</summary>
[Trait("Category", "Integration")]
public sealed class S3BlobStoreContractTests(RustFsFixture s3) : BlobStoreContractTests, IClassFixture<RustFsFixture>
{
    private const string ContractBucket = "contract-tests";

    private readonly List<IDisposable> _owned = [];

    protected override bool SupportsDownloadUrls => true;

    protected override ValueTask<IBlobStore> CreateStoreAsync() =>
        ValueTask.FromResult<IBlobStore>(CreateStore(ContractBucket, createIfMissing: true));

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
        url.Scheme.ShouldBe(Uri.UriSchemeHttp);
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(content);
    }

    [Fact]
    public async Task GetDownloadUrlAsync_TamperedSignature_IsRejectedByServer()
    {
        var key = NewKey("download.bin");
        await PutAsync(key, [1, 2, 3]);
        var url = await Store.GetDownloadUrlAsync(key, TimeSpan.FromMinutes(5), Ct);
        url.ShouldNotBeNull();

        S3BlobStore.SignedLifetimeSeconds(url).ShouldBe(300);
        var tampered = new Uri(url.AbsoluteUri.Replace("X-Amz-Expires=300", "X-Amz-Expires=301", StringComparison.Ordinal));
        tampered.ShouldNotBe(url);
        using var http = new HttpClient();
        using var response = await http.GetAsync(tampered, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(5, 300)]
    [InlineData(15, 900)]
    [InlineData(60, 900)]
    [InlineData(24 * 60, 900)]
    public async Task GetDownloadUrlAsync_Lifetime_IsCappedAtFifteenMinutes(int minutes, int expectedSeconds)
    {
        var url = await Store.GetDownloadUrlAsync(NewKey("file.bin"), TimeSpan.FromMinutes(minutes), Ct);

        url.ShouldNotBeNull();
        HttpUtility.ParseQueryString(url.Query)["X-Amz-Expires"].ShouldBe(expectedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
    public async Task PutAsync_SeekableStream_IsNotClosed()
    {
        using var stream = new MemoryStream([1, 2, 3]);

        await Store.PutAsync(NewKey("file.bin"), stream, "application/octet-stream", Ct);

        stream.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task PutAsync_Key_IsStoredVerbatimWithContentType()
    {
        var key = NewKey("nested/dir/file.txt");
        using var stream = new MemoryStream([1]);

        await Store.PutAsync(key, stream, "text/plain", Ct);

        using var client = CreateClient();
        var metadata = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = ContractBucket, Key = key.Value }, Ct);
        metadata.Headers.ContentType.ShouldBe("text/plain");
        var listed = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = ContractBucket, Prefix = key.Value }, Ct);
        listed.S3Objects.ShouldHaveSingleItem().Key.ShouldBe(key.Value);
    }

    [Fact]
    public async Task Operations_MissingBucketWithoutCreateIfMissing_ThrowNamingKeyAndDoNotCreate()
    {
        var bucket = UniqueBucket();
        var store = CreateStore(bucket, createIfMissing: false);
        var key = NewKey("file.bin");

        var put = await Should.ThrowAsync<InvalidOperationException>(() => store.PutAsync(key, new MemoryStream([1]), "application/octet-stream", Ct));
        put.Message.ShouldContain("Blob:S3:Bucket");
        put.Message.ShouldContain(bucket);
        put.Message.ShouldContain("Blob:S3:CreateIfMissing");
        (await Should.ThrowAsync<InvalidOperationException>(() => store.OpenReadAsync(key, Ct))).Message.ShouldContain("Blob:S3:Bucket");
        (await Should.ThrowAsync<InvalidOperationException>(() => store.ExistsAsync(key, Ct))).Message.ShouldContain("Blob:S3:Bucket");
        (await Should.ThrowAsync<InvalidOperationException>(() => store.DeleteAsync(key, Ct))).Message.ShouldContain("Blob:S3:Bucket");
        (await Should.ThrowAsync<InvalidOperationException>(() => store.GetDownloadUrlAsync(key, TimeSpan.FromMinutes(1), Ct))).Message.ShouldContain("Blob:S3:Bucket");

        (await BucketExistsAsync(bucket)).ShouldBeFalse();
    }

    [Fact]
    public async Task PutAsync_MissingBucketWithCreateIfMissing_CreatesBucketAndStores()
    {
        var bucket = UniqueBucket();
        var store = CreateStore(bucket, createIfMissing: true);
        var key = NewKey("file.bin");

        await store.PutAsync(key, new MemoryStream([7, 8]), "application/octet-stream", Ct);

        (await BucketExistsAsync(bucket)).ShouldBeTrue();
        (await store.ExistsAsync(key, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Operations_BucketDeletedAfterFirstUse_ThrowNamingKey()
    {
        var bucket = UniqueBucket();
        var store = CreateStore(bucket, createIfMissing: false);
        using var client = CreateClient();
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, Ct);
        (await store.ExistsAsync(NewKey("file.bin"), Ct)).ShouldBeFalse();

        await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }, Ct);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => store.OpenReadAsync(NewKey("file.bin"), Ct));
        exception.Message.ShouldContain("Blob:S3:Bucket");
    }

    [Fact]
    public async Task Operations_WrongSecretKey_FailWithoutEchoingSecret()
    {
        const string wrongSecret = "wrong-secret-value-5d1e";
        var options = NewOptions(ContractBucket, createIfMissing: false);
        options.SecretKey = wrongSecret;
        var store = CreateStore(options);

        var exception = await Should.ThrowAsync<Exception>(() => store.ExistsAsync(NewKey("file.bin"), Ct));

        exception.ToString().ShouldNotContain(wrongSecret);
    }

    private S3BlobStore CreateStore(string bucket, bool createIfMissing) => CreateStore(NewOptions(bucket, createIfMissing));

    private S3BlobStore CreateStore(S3BlobOptions options)
    {
        var client = S3BlobStore.CreateClient(options);
        var store = new S3BlobStore(client, Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System, NullLogger<S3BlobStore>.Instance);
        _owned.Add(store);
        _owned.Add(client);
        return store;
    }

    private S3BlobOptions NewOptions(string bucket, bool createIfMissing) => new()
    {
        ServiceUrl = s3.ServiceUrl,
        Region = RustFsFixture.Region,
        Bucket = bucket,
        AccessKey = RustFsFixture.AccessKey,
        SecretKey = RustFsFixture.SecretKey,
        ForcePathStyle = true,
        CreateIfMissing = createIfMissing,
    };

    private AmazonS3Client CreateClient() => S3BlobStore.CreateClient(NewOptions(ContractBucket, createIfMissing: false));

    private async Task<bool> BucketExistsAsync(string bucket)
    {
        using var client = CreateClient();
        try
        {
            await client.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }, Ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static string UniqueBucket() => "missing-" + Guid.NewGuid().ToString("N");
}
