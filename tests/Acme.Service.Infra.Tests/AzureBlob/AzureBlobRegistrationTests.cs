using System.Web;
using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Infra.AzureBlob;
using Acme.Service.BuildingBlocks.Providers;
using Acme.Service.Host.Providers;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Acme.Service.Infra.Tests.AzureBlob;

public sealed class AzureBlobRegistrationTests
{
    private const string AccountKey = "c2VjcmV0LWFjY291bnQta2V5LTkxYzItbm90LXJlYWw=";
    private const string ConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=unitaccount;AccountKey=" + AccountKey + ";EndpointSuffix=core.windows.net";

    [Fact]
    public void Registration_DeclaresBlobCategoryAndAzureBlobName()
    {
        var registration = new AzureBlobRegistration();

        registration.Category.ShouldBe(ProviderCategory.Blob);
        registration.Name.ShouldBe("AzureBlob");
        ProviderRegistry.IsDevelopmentOnly(ProviderCategory.Blob, registration.Name).ShouldBeFalse();
    }

    [Fact]
    public async Task Register_ConnectionString_ResolvesSingletonAzureBlobStoreWithSharedKey()
    {
        await using var provider = Build(new() { ["Blob:AzureBlob:ConnectionString"] = ConnectionString, ["Blob:AzureBlob:Container"] = "uploads" });

        var store = provider.GetRequiredService<IBlobStore>();

        store.ShouldBeOfType<AzureBlobStore>();
        provider.GetRequiredService<IBlobStore>().ShouldBeSameAs(store);
        var client = provider.GetRequiredService<BlobServiceClient>();
        client.AccountName.ShouldBe("unitaccount");
        client.CanGenerateAccountSasUri.ShouldBeTrue();
        provider.GetRequiredService<IOptions<AzureBlobOptions>>().Value.CreateIfMissing.ShouldBeFalse();
    }

    [Fact]
    public async Task Register_ServiceUri_ResolvesStoreWithTokenCredentialWithoutNetwork()
    {
        await using var provider = Build(new()
        {
            ["Blob:AzureBlob:ServiceUri"] = "https://unitaccount.blob.core.windows.net",
            ["Blob:AzureBlob:Container"] = "uploads",
            ["Blob:AzureBlob:CreateIfMissing"] = "true",
        });

        provider.GetRequiredService<IBlobStore>().ShouldBeOfType<AzureBlobStore>();
        var client = provider.GetRequiredService<BlobServiceClient>();
        client.Uri.ShouldBe(new Uri("https://unitaccount.blob.core.windows.net"));
        client.CanGenerateAccountSasUri.ShouldBeFalse();
        provider.GetRequiredService<IOptions<AzureBlobOptions>>().Value.CreateIfMissing.ShouldBeTrue();
    }

    [Fact]
    public async Task Register_NeitherAuthSetting_ValidationNamesBothKeys()
    {
        await using var provider = Build(new() { ["Blob:AzureBlob:Container"] = "uploads" });

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureBlobOptions>>().Value);

        exception.Message.ShouldContain("Blob:AzureBlob:ConnectionString");
        exception.Message.ShouldContain("Blob:AzureBlob:ServiceUri");
    }

    [Fact]
    public async Task Register_BothAuthSettings_ValidationNamesBothKeysWithoutEchoingSecret()
    {
        await using var provider = Build(new()
        {
            ["Blob:AzureBlob:ConnectionString"] = ConnectionString,
            ["Blob:AzureBlob:ServiceUri"] = "https://unitaccount.blob.core.windows.net",
            ["Blob:AzureBlob:Container"] = "uploads",
        });

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureBlobOptions>>().Value);

        exception.Message.ShouldContain("Blob:AzureBlob:ConnectionString");
        exception.Message.ShouldContain("Blob:AzureBlob:ServiceUri");
        exception.Message.ShouldNotContain(AccountKey);
    }

    [Fact]
    public async Task Register_MissingContainer_ValidationNamesKey()
    {
        await using var provider = Build(new() { ["Blob:AzureBlob:ConnectionString"] = ConnectionString });

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureBlobOptions>>().Value);

        exception.Message.ShouldContain("Blob:AzureBlob:Container is required");
    }

    [Theory]
    [InlineData("Blob:AzureBlob:Container", "Uploads")]
    [InlineData("Blob:AzureBlob:Container", "ab")]
    [InlineData("Blob:AzureBlob:Container", "up--loads")]
    [InlineData("Blob:AzureBlob:Container", "-uploads")]
    [InlineData("Blob:AzureBlob:Container", "up_loads")]
    [InlineData("Blob:AzureBlob:CreateIfMissing", "yes")]
    public async Task Register_InvalidSetting_ValidationNamesKey(string key, string value)
    {
        await using var provider = Build(new()
        {
            ["Blob:AzureBlob:ConnectionString"] = ConnectionString,
            ["Blob:AzureBlob:Container"] = "uploads",
            [key] = value,
        });

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureBlobOptions>>().Value);

        exception.Message.ShouldContain(key);
        exception.Message.ShouldNotContain(AccountKey);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://unitaccount.blob.core.windows.net")]
    [InlineData("/relative")]
    public async Task Register_InvalidServiceUri_ValidationNamesKey(string value)
    {
        await using var provider = Build(new() { ["Blob:AzureBlob:ServiceUri"] = value, ["Blob:AzureBlob:Container"] = "uploads" });

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureBlobOptions>>().Value);

        exception.Message.ShouldContain("Blob:AzureBlob:ServiceUri");
    }

    [Theory]
    [InlineData("AccountKey=" + AccountKey)]
    [InlineData("garbage;AccountKey=" + AccountKey)]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=;AccountKey=" + AccountKey + ";BlobEndpoint=not-a-uri")]
    public async Task Register_InvalidConnectionString_ValidationNamesKeyWithoutEchoingSecret(string value)
    {
        await using var provider = Build(new() { ["Blob:AzureBlob:ConnectionString"] = value, ["Blob:AzureBlob:Container"] = "uploads" });

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureBlobOptions>>().Value);

        exception.Message.ShouldContain("Blob:AzureBlob:ConnectionString is not a valid");
        exception.Message.ShouldNotContain(AccountKey);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(15 * 60, 15 * 60)]
    [InlineData(15 * 60 + 1, 15 * 60)]
    [InlineData(7 * 24 * 3600, 15 * 60)]
    public void ClampLifetime_PositiveLifetime_IsCappedAtFifteenMinutes(int seconds, int expectedSeconds) =>
        AzureBlobStore.ClampLifetime(TimeSpan.FromSeconds(seconds)).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ClampLifetime_NonPositiveLifetime_Throws(int seconds) =>
        Should.Throw<ArgumentOutOfRangeException>(() => AzureBlobStore.ClampLifetime(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void UploadTransferOptions_AreBounded()
    {
        var options = AzureBlobStore.UploadTransferOptions;

        options.InitialTransferSize.ShouldBe(8L * 1024 * 1024);
        options.MaximumTransferSize.ShouldBe(8L * 1024 * 1024);
        options.MaximumConcurrency.ShouldBe(4);
    }

    [Fact]
    public async Task GetDownloadUrlAsync_TokenCredential_ReturnsUserDelegationSasAndCachesKey()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var time = new Fixtures.ManualTimeProvider(now);
        var (service, keyRequests) = DelegationService();
        using var store = new AzureBlobStore(
            service,
            Microsoft.Extensions.Options.Options.Create(new AzureBlobOptions { ServiceUri = "https://unitaccount.blob.core.windows.net", Container = "uploads" }),
            time,
            NullLogger<AzureBlobStore>.Instance);

        var first = await store.GetDownloadUrlAsync(BlobKey.Create("orders/1/invoice.pdf"), TimeSpan.FromHours(2), TestContext.Current.CancellationToken);
        var second = await store.GetDownloadUrlAsync(BlobKey.Create("orders/2/invoice.pdf"), TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        first.ShouldNotBeNull();
        first.GetLeftPart(UriPartial.Path).ShouldBe("https://unitaccount.blob.core.windows.net/uploads/orders/1/invoice.pdf");
        var query = HttpUtility.ParseQueryString(first.Query);
        query["sp"].ShouldBe("r");
        query["sr"].ShouldBe("b");
        query["skoid"].ShouldBe("00000000-0000-0000-0000-0000000000aa");
        query["sig"].ShouldNotBeNullOrEmpty();
        DateTimeOffset.Parse(query["se"]!, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(now.AddMinutes(15));
        second.ShouldNotBeNull();
        keyRequests.Count.ShouldBe(1);
        keyRequests[0].ShouldBe((now.AddMinutes(-5), now.AddHours(1)));
    }

    [Fact]
    public async Task GetDownloadUrlAsync_CachedKeyExpiresBeforeSas_RequestsNewKey()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var time = new Fixtures.ManualTimeProvider(now);
        var (service, keyRequests) = DelegationService();
        using var store = new AzureBlobStore(
            service,
            Microsoft.Extensions.Options.Options.Create(new AzureBlobOptions { ServiceUri = "https://unitaccount.blob.core.windows.net", Container = "uploads" }),
            time,
            NullLogger<AzureBlobStore>.Instance);

        await store.GetDownloadUrlAsync(BlobKey.Create("a.txt"), TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(50));
        await store.GetDownloadUrlAsync(BlobKey.Create("a.txt"), TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        keyRequests.Count.ShouldBe(2);
    }

    private static (BlobServiceClient Service, List<(DateTimeOffset? StartsOn, DateTimeOffset ExpiresOn)> KeyRequests) DelegationService()
    {
        var keyRequests = new List<(DateTimeOffset? StartsOn, DateTimeOffset ExpiresOn)>();
        var account = new Uri("https://unitaccount.blob.core.windows.net");

        // A token-credential client cannot sign with an account key, so the store must use a user delegation SAS.
        var container = Substitute.For<BlobContainerClient>();
        container.Name.Returns("uploads");
        container.ExistsAsync(Arg.Any<CancellationToken>()).Returns(Response.FromValue(true, Substitute.For<Response>()));
        container.GetBlobClient(Arg.Any<string>()).Returns(ci => new BlobClient(new Uri(account, "uploads/" + ci.Arg<string>())));

        var service = Substitute.For<BlobServiceClient>();
        service.GetBlobContainerClient("uploads").Returns(container);
        service.GetUserDelegationKeyAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var startsOn = ci.ArgAt<DateTimeOffset?>(0);
                var expiresOn = ci.ArgAt<DateTimeOffset>(1);
                keyRequests.Add((startsOn, expiresOn));
                var key = BlobsModelFactory.UserDelegationKey(
                    "00000000-0000-0000-0000-0000000000aa",
                    "00000000-0000-0000-0000-0000000000bb",
                    startsOn ?? DateTimeOffset.MinValue,
                    expiresOn,
                    "b",
                    "2026-10-06",
                    Convert.ToBase64String(new byte[32]));
                return Response.FromValue(key, Substitute.For<Response>());
            });
        return (service, keyRequests);
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new AzureBlobRegistration().Register(services, configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
