using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Infra.S3;
using Acme.Service.BuildingBlocks.Providers;
using Acme.Service.Host.Providers;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.Infra.Tests.S3;

public sealed class S3BlobRegistrationTests
{
    private const string Secret = "s3-secret-value-91c2";

    private static readonly Dictionary<string, string?> _valid = new()
    {
        ["Blob:S3:ServiceUrl"] = "http://127.0.0.1:1",
        ["Blob:S3:Region"] = "us-east-1",
        ["Blob:S3:Bucket"] = "uploads",
        ["Blob:S3:AccessKey"] = "access",
        ["Blob:S3:SecretKey"] = Secret,
        ["Blob:S3:ForcePathStyle"] = "true",
    };

    [Fact]
    public void Registration_DeclaresBlobCategoryAndS3Name()
    {
        var registration = new S3BlobRegistration();

        registration.Category.ShouldBe(ProviderCategory.Blob);
        registration.Name.ShouldBe("S3");
        ProviderRegistry.IsDevelopmentOnly(ProviderCategory.Blob, registration.Name).ShouldBeFalse();
    }

    [Fact]
    public async Task Register_ValidConfiguration_ResolvesSingletonS3BlobStoreWithoutNetwork()
    {
        await using var provider = Build(_valid);

        var store = provider.GetRequiredService<IBlobStore>();

        store.ShouldBeOfType<S3BlobStore>();
        provider.GetRequiredService<IBlobStore>().ShouldBeSameAs(store);
        provider.GetRequiredService<IAmazonS3>().ShouldBeOfType<AmazonS3Client>();
    }

    [Fact]
    public async Task Register_ConfiguredValues_AreBoundWithDefaults()
    {
        await using var provider = Build(_valid);

        var options = provider.GetRequiredService<IOptions<S3BlobOptions>>().Value;

        options.ServiceUrl.ShouldBe("http://127.0.0.1:1");
        options.Region.ShouldBe("us-east-1");
        options.Bucket.ShouldBe("uploads");
        options.ForcePathStyle.ShouldBeTrue();
        options.CreateIfMissing.ShouldBeFalse();
    }

    [Fact]
    public async Task Register_RegionOnlyWithoutCredentials_IsValid()
    {
        // No keys: the default AWS credential chain (IRSA, instance role, environment) is used.
        await using var provider = Build(new() { ["Blob:S3:Region"] = "eu-west-1", ["Blob:S3:Bucket"] = "uploads" });

        var options = provider.GetRequiredService<IOptions<S3BlobOptions>>().Value;

        options.HasStaticCredentials.ShouldBeFalse();
        options.ForcePathStyle.ShouldBeFalse();
        provider.GetRequiredService<IBlobStore>().ShouldBeOfType<S3BlobStore>();
    }

    [Fact]
    public void CreateClient_ServiceUrl_SetsPathStyleSigningRegionAndChecksumsWhenRequired()
    {
        using var client = S3BlobStore.CreateClient(new S3BlobOptions
        {
            ServiceUrl = "http://127.0.0.1:9000",
            Region = "garage",
            Bucket = "uploads",
            AccessKey = "access",
            SecretKey = Secret,
            ForcePathStyle = true,
        });

        var config = (AmazonS3Config)client.Config;
        config.ServiceURL.ShouldStartWith("http://127.0.0.1:9000");
        config.ForcePathStyle.ShouldBeTrue();
        config.AuthenticationRegion.ShouldBe("garage");
        config.RequestChecksumCalculation.ShouldBe(RequestChecksumCalculation.WHEN_REQUIRED);
        config.ResponseChecksumValidation.ShouldBe(ResponseChecksumValidation.WHEN_REQUIRED);
    }

    [Fact]
    public void CreateClient_RegionOnly_UsesRegionEndpoint()
    {
        using var client = S3BlobStore.CreateClient(new S3BlobOptions { Region = "eu-west-1", Bucket = "uploads" });

        var config = (AmazonS3Config)client.Config;
        config.RegionEndpoint.SystemName.ShouldBe("eu-west-1");
        config.ServiceURL.ShouldBeNull();
        config.RequestChecksumCalculation.ShouldBe(RequestChecksumCalculation.WHEN_REQUIRED);
        config.ResponseChecksumValidation.ShouldBe(ResponseChecksumValidation.WHEN_REQUIRED);
    }

    [Fact]
    public async Task Register_MissingBucket_ValidationNamesKey()
    {
        await using var provider = Build(Without(_valid, "Blob:S3:Bucket"));

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<S3BlobOptions>>().Value);

        exception.Message.ShouldContain("Blob:S3:Bucket is required");
    }

    [Fact]
    public async Task Register_NeitherRegionNorServiceUrl_ValidationNamesBothKeys()
    {
        await using var provider = Build(Without(Without(_valid, "Blob:S3:Region"), "Blob:S3:ServiceUrl"));

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<S3BlobOptions>>().Value);

        exception.Message.ShouldContain("Blob:S3:Region");
        exception.Message.ShouldContain("Blob:S3:ServiceUrl");
    }

    [Theory]
    [InlineData("Blob:S3:ServiceUrl", "not a url")]
    [InlineData("Blob:S3:ServiceUrl", "ftp://example.com")]
    [InlineData("Blob:S3:ServiceUrl", "/relative")]
    [InlineData("Blob:S3:Region", "EU West 1")]
    [InlineData("Blob:S3:Bucket", "Uploads")]
    [InlineData("Blob:S3:Bucket", "ab")]
    [InlineData("Blob:S3:Bucket", "my_bucket")]
    [InlineData("Blob:S3:Bucket", "-uploads")]
    [InlineData("Blob:S3:Bucket", "up..loads")]
    [InlineData("Blob:S3:Bucket", "192.168.1.1")]
    [InlineData("Blob:S3:ForcePathStyle", "yes")]
    [InlineData("Blob:S3:CreateIfMissing", "1")]
    public async Task Register_InvalidSetting_ValidationNamesKey(string key, string value)
    {
        await using var provider = Build(With(_valid, key, value));

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<S3BlobOptions>>().Value);

        exception.Message.ShouldContain(key);
        exception.Message.ShouldNotContain(Secret);
    }

    [Theory]
    [InlineData("Blob:S3:AccessKey")]
    [InlineData("Blob:S3:SecretKey")]
    public async Task Register_OnlyOneCredential_ValidationNamesBothKeysWithoutEchoingSecret(string removed)
    {
        await using var provider = Build(Without(_valid, removed));

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<S3BlobOptions>>().Value);

        exception.Message.ShouldContain("Blob:S3:AccessKey");
        exception.Message.ShouldContain("Blob:S3:SecretKey");
        exception.Message.ShouldNotContain(Secret);
    }

    [Fact]
    public async Task Register_ManyInvalidSettings_ReportsAllWithoutEchoingSecret()
    {
        await using var provider = Build(new()
        {
            ["Blob:S3:ServiceUrl"] = "nope",
            ["Blob:S3:SecretKey"] = Secret,
            ["Blob:S3:CreateIfMissing"] = "maybe",
        });

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<S3BlobOptions>>().Value);

        exception.Failures.Count().ShouldBe(4);
        exception.Message.ShouldNotContain(Secret);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5 * 60, 5 * 60)]
    [InlineData(15 * 60, 15 * 60)]
    [InlineData(15 * 60 + 1, 15 * 60)]
    [InlineData(7 * 24 * 3600, 15 * 60)]
    public void ClampLifetime_PositiveLifetime_IsCappedAtFifteenMinutes(int seconds, int expectedSeconds) =>
        S3BlobStore.ClampLifetime(TimeSpan.FromSeconds(seconds)).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ClampLifetime_NonPositiveLifetime_Throws(int seconds) =>
        Should.Throw<ArgumentOutOfRangeException>(() => S3BlobStore.ClampLifetime(TimeSpan.FromSeconds(seconds)));

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new S3BlobRegistration().Register(services, configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static Dictionary<string, string?> With(Dictionary<string, string?> settings, string key, string? value) =>
        new(settings) { [key] = value };

    private static Dictionary<string, string?> Without(Dictionary<string, string?> settings, string key)
    {
        var copy = new Dictionary<string, string?>(settings);
        copy.Remove(key);
        return copy;
    }
}
