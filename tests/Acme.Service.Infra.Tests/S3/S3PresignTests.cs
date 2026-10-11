using System.Globalization;
using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Infra.S3;
using Acme.Service.Infra.Tests.Fixtures;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Acme.Service.Infra.Tests.S3;

/// <summary>
/// The AWS SDK signs <c>X-Amz-Expires</c> against its own clock-skew-corrected clock; the store must still return a
/// URL whose signed lifetime is exactly the requested one and never longer than 15 minutes.
/// </summary>
public sealed class S3PresignTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0.0, 300, 300)]
    [InlineData(-1.4, 300, 300)]
    [InlineData(2.6, 300, 300)]
    [InlineData(-90.0, 3600, 900)]
    [InlineData(45.0, 900, 900)]
    [InlineData(0.0, 0.5, 1)]
    public async Task GetDownloadUrlAsync_SdkClockSkew_SignsExactRequestedLifetime(double sdkSkewSeconds, double requestedSeconds, long expectedSeconds)
    {
        var client = SkewedClient(TimeSpan.FromSeconds(sdkSkewSeconds));
        using var store = CreateStore(client);

        var url = await store.GetDownloadUrlAsync(BlobKey.Create("a/b.txt"), TimeSpan.FromSeconds(requestedSeconds), TestContext.Current.CancellationToken);

        url.ShouldNotBeNull();
        S3BlobStore.SignedLifetimeSeconds(url).ShouldBe(expectedSeconds);
    }

    [Fact]
    public async Task GetDownloadUrlAsync_SignedLifetimeAlwaysTooLong_ThrowsInsteadOfReturningUrl()
    {
        var client = BucketClient();
        client.GetPreSignedURLAsync(Arg.Any<GetPreSignedUrlRequest>()).Returns("http://store/b/k?X-Amz-Expires=901&X-Amz-Signature=x");
        using var store = CreateStore(client);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            store.GetDownloadUrlAsync(BlobKey.Create("k"), TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken));
        await client.Received(3).GetPreSignedURLAsync(Arg.Any<GetPreSignedUrlRequest>());
    }

    [Fact]
    public async Task GetDownloadUrlAsync_NoSignedLifetime_Throws()
    {
        var client = BucketClient();
        client.GetPreSignedURLAsync(Arg.Any<GetPreSignedUrlRequest>()).Returns("http://store/b/k?X-Amz-Signature=x");
        using var store = CreateStore(client);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            store.GetDownloadUrlAsync(BlobKey.Create("k"), TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetDownloadUrlAsync_Request_IsGetForBucketAndVerbatimKeyOverHttpForHttpServiceUrl()
    {
        var client = SkewedClient(TimeSpan.Zero);
        using var store = CreateStore(client);

        await store.GetDownloadUrlAsync(BlobKey.Create("orders/1/invoice.pdf"), TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        await client.Received(1).GetPreSignedURLAsync(Arg.Is<GetPreSignedUrlRequest>(r =>
            r.BucketName == "uploads" && r.Key == "orders/1/invoice.pdf" && r.Verb == HttpVerb.GET && r.Protocol == Protocol.HTTP));
    }

    [Theory]
    [InlineData("http://h/b/k?X-Amz-Algorithm=AWS4&X-Amz-Expires=300&X-Amz-Signature=s", 300L)]
    [InlineData("http://h/b/k?x-amz-expires=42", 42L)]
    [InlineData("http://h/b/k?X-Amz-Expires=-5", null)]
    [InlineData("http://h/b/k?X-Amz-Expires=", null)]
    [InlineData("http://h/b/k", null)]
    public void SignedLifetimeSeconds_ParsesQuery(string url, long? expected) =>
        S3BlobStore.SignedLifetimeSeconds(new Uri(url)).ShouldBe(expected);

    private static S3BlobStore CreateStore(IAmazonS3 client) =>
        new(
            client,
            Microsoft.Extensions.Options.Options.Create(new S3BlobOptions { ServiceUrl = "http://store:9000", Bucket = "uploads" }),
            new ManualTimeProvider(_now),
            NullLogger<S3BlobStore>.Instance);

    private static IAmazonS3 BucketClient()
    {
        var client = Substitute.For<IAmazonS3>();
        client.HeadBucketAsync(Arg.Any<HeadBucketRequest>(), Arg.Any<CancellationToken>()).Returns(new HeadBucketResponse());
        return client;
    }

    /// <summary>A client that signs like the SDK: lifetime = round(Expires - its corrected clock).</summary>
    private static IAmazonS3 SkewedClient(TimeSpan sdkClockSkew)
    {
        var client = BucketClient();
        var sdkNow = _now.UtcDateTime + sdkClockSkew;
        client.GetPreSignedURLAsync(Arg.Any<GetPreSignedUrlRequest>()).Returns(ci =>
        {
            var request = ci.Arg<GetPreSignedUrlRequest>();
            var seconds = Convert.ToInt64((request.Expires!.Value - sdkNow).TotalSeconds);
            return string.Create(CultureInfo.InvariantCulture, $"http://store:9000/uploads/{request.Key}?X-Amz-Expires={seconds}&X-Amz-Signature=sig");
        });
        return client;
    }
}
