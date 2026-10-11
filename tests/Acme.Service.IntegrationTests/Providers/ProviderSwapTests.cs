using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Caching;
using Acme.Service.IntegrationTests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.IntegrationTests.Providers;

/// <summary>
/// Provider swap (D-02, INV-24): the same host binary, configured with every <c>Cache:Provider</c> x
/// <c>Blob:Provider</c> combination, behaves the same, with no code change.
/// </summary>
public sealed class ProviderSwapTests(PostgresFixture postgres, RedisFixture redis, RustFsFixture rustFs, AzuriteFixture azurite)
    : IClassFixture<PostgresFixture>, IClassFixture<RedisFixture>, IClassFixture<RustFsFixture>, IClassFixture<AzuriteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Combinations()
    {
        var data = new TheoryData<string, string>();
        foreach (var cache in new[] { "InMemory", "Redis" })
        {
            foreach (var blob in new[] { "Local", "S3", "AzureBlob" })
            {
                data.Add(cache, blob);
            }
        }

        return data;
    }

    [Theory]
    [Trait("Category", "Integration")]
    [MemberData(nameof(Combinations))]
    public async Task Ports_AnyProviderCombination_BehaveTheSame(string cacheProvider, string blobProvider)
    {
        await using var factory = await CreateFactoryAsync(cacheProvider, blobProvider);
        var cache = factory.Services.GetRequiredService<ICache>();
        var blobs = factory.Services.GetRequiredService<IBlobStore>();

        cache.GetType().Namespace!.ShouldEndWith(ProviderNamespace(cacheProvider));
        blobs.GetType().Namespace!.ShouldEndWith(ProviderNamespace(blobProvider));

        // Cache: set/get, atomic add-if-absent, remove.
        var key = "swap:" + Guid.NewGuid().ToString("N");
        await cache.SetAsync(key, new CachedValue("first", 1), TimeSpan.FromMinutes(1), Ct);
        (await cache.GetAsync<CachedValue>(key, Ct)).ShouldBe(new CachedValue("first", 1));
        (await cache.TryAddAsync(key, new CachedValue("second", 2), TimeSpan.FromMinutes(1), Ct)).ShouldBeFalse();
        (await cache.GetAsync<CachedValue>(key, Ct)).ShouldBe(new CachedValue("first", 1));
        await cache.RemoveAsync(key, Ct);
        (await cache.GetAsync<CachedValue>(key, Ct)).ShouldBeNull();
        (await cache.TryAddAsync(key, new CachedValue("third", 3), TimeSpan.FromMinutes(1), Ct)).ShouldBeTrue();
        cache.IsAvailable.ShouldBeTrue();

        // Blob: put, exists, read back, download URL (null when the provider has none), delete.
        var blobKey = BlobKey.Create($"swap/{Guid.NewGuid():N}.txt");
        var content = Encoding.UTF8.GetBytes("provider swap " + Guid.NewGuid());
        using (var upload = new MemoryStream(content))
        {
            await blobs.PutAsync(blobKey, upload, "text/plain", Ct);
        }

        (await blobs.ExistsAsync(blobKey, Ct)).ShouldBeTrue();
        await using (var read = await blobs.OpenReadAsync(blobKey, Ct))
        {
            read.ShouldNotBeNull();
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy, Ct);
            copy.ToArray().ShouldBe(content);
        }

        var url = await blobs.GetDownloadUrlAsync(blobKey, TimeSpan.FromMinutes(5), Ct);
        if (blobProvider == "Local")
        {
            url.ShouldBeNull();
        }
        else
        {
            url.ShouldNotBeNull();
            (await FetchAsync(url)).ShouldBe(content);
        }

        await blobs.DeleteAsync(blobKey, Ct);
        (await blobs.ExistsAsync(blobKey, Ct)).ShouldBeFalse();
        (await blobs.OpenReadAsync(blobKey, Ct)).ShouldBeNull();

        // The host is ready with either combination (cache and blob never make it Unhealthy).
        using var client = factory.CreateClient();
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // <sample:orders>
    [Theory]
    [Trait("Category", "Integration")]
    [MemberData(nameof(Combinations))]
    public async Task OrdersApi_AnyProviderCombination_SameIdempotencyAndAttachmentBehaviour(string cacheProvider, string blobProvider)
    {
        await using var factory = await CreateFactoryAsync(cacheProvider, blobProvider);
        var api = new ServiceApi(factory);
        var customer = await api.UserAsync($"swap-{Guid.NewGuid():N}@example.com");
        var key = ("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var order = new { currency = "EUR", lines = new[] { new { sku = "SKU-1", quantity = 2, unitPrice = 9.5m } } };

        using var created = await api.PostAsync("/api/v1/orders", order, customer.AccessToken, key);
        using var replayed = await api.PostAsync("/api/v1/orders", order, customer.AccessToken, key);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        replayed.StatusCode.ShouldBe(HttpStatusCode.Created);
        var orderId = (await ServiceApi.JsonAsync(created)).GetProperty("id").GetGuid();
        (await ServiceApi.JsonAsync(replayed)).GetProperty("id").GetGuid().ShouldBe(orderId);
        (await api.ScalarAsync<long>("SELECT count(*) FROM orders.orders WHERE customer_id = @id", ("id", customer.Id))).ShouldBe(1);

        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 2048));
        using var file = new ByteArrayContent(pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        using var form = new MultipartFormDataContent { { file, "file", "swap.pdf" } };
        using var uploaded = await api.SendAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/attachments", customer.AccessToken, form);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(Ct));

        using var download = await api.GetAsync(uploaded.Headers.Location!.ToString(), customer.AccessToken);
        if (blobProvider == "Local")
        {
            download.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await download.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(pdf);
        }
        else
        {
            download.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            (await FetchAsync(download.Headers.Location!)).ShouldBe(pdf);
        }
    }

    // </sample:orders>
    private static string ProviderNamespace(string provider) => provider switch
    {
        "InMemory" or "Local" => ".Infra.InMemory",
        "Redis" => ".Infra.Redis",
        "S3" => ".Infra.S3",
        "AzureBlob" => ".Infra.AzureBlob",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider."),
    };

    private static async Task<byte[]> FetchAsync(Uri url)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    private async Task<IntegrationWebFactory> CreateFactoryAsync(string cacheProvider, string blobProvider)
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await ServiceApi.MigrateAsync(connectionString);
        var store = "swap-" + Guid.NewGuid().ToString("N")[..12];
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cache:Provider"] = cacheProvider,
            ["Blob:Provider"] = blobProvider,
        };
        if (cacheProvider == "Redis")
        {
            settings["Cache:Redis:ConnectionString"] = redis.ConnectionString;
        }

        if (blobProvider == "S3")
        {
            settings["Blob:S3:ServiceUrl"] = rustFs.ServiceUrl;
            settings["Blob:S3:Region"] = RustFsFixture.Region;
            settings["Blob:S3:Bucket"] = store;
            settings["Blob:S3:AccessKey"] = RustFsFixture.AccessKey;
            settings["Blob:S3:SecretKey"] = RustFsFixture.SecretKey;
            settings["Blob:S3:ForcePathStyle"] = "true";
            settings["Blob:S3:CreateIfMissing"] = "true";
        }
        else if (blobProvider == "AzureBlob")
        {
            settings["Blob:AzureBlob:ConnectionString"] = azurite.ConnectionString;
            settings["Blob:AzureBlob:Container"] = store;
            settings["Blob:AzureBlob:CreateIfMissing"] = "true";
        }

        return new IntegrationWebFactory(connectionString, settings);
    }

    private sealed record CachedValue(string Name, int Number);
}
