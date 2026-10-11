using Acme.Service.BuildingBlocks.Caching;
using Acme.Service.BuildingBlocks.Infra.Redis;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.Infra.Tests.Redis;

public sealed class RedisCacheRegistrationTests
{
    private const string Password = "registration-secret-91c2";

    [Fact]
    public void Registration_DeclaresCacheCategoryAndRedisName()
    {
        var registration = new RedisCacheRegistration();

        registration.Category.ShouldBe(ProviderCategory.Cache);
        registration.Name.ShouldBe("Redis");
    }

    [Fact]
    public async Task Register_ValidConfiguration_ResolvesSingletonRedisCacheWithoutConnecting()
    {
        await using var provider = Build(new() { ["Cache:Redis:ConnectionString"] = "127.0.0.1:1" });

        var cache = provider.GetRequiredService<ICache>();

        cache.ShouldBeOfType<RedisCache>();
        provider.GetRequiredService<ICache>().ShouldBeSameAs(cache);
        cache.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task Register_Defaults_AreFiveHundredMillisecondsAndOneMebibyte()
    {
        await using var provider = Build(new() { ["Cache:Redis:ConnectionString"] = "127.0.0.1:1" });

        var options = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;

        options.ConnectTimeout.ShouldBe(TimeSpan.FromMilliseconds(500));
        options.SyncTimeout.ShouldBe(TimeSpan.FromMilliseconds(500));
        options.MaxValueBytes.ShouldBe(1024 * 1024);

        var configuration = options.ToConfigurationOptions();
        configuration.AbortOnConnectFail.ShouldBeFalse();
        configuration.ConnectTimeout.ShouldBe(500);
        configuration.SyncTimeout.ShouldBe(500);
        configuration.AsyncTimeout.ShouldBe(500);
    }

    [Fact]
    public async Task Register_ConfiguredValues_AreBound()
    {
        await using var provider = Build(new()
        {
            ["Cache:Redis:ConnectionString"] = "127.0.0.1:1",
            ["Cache:Redis:ConnectTimeout"] = "00:00:02",
            ["Cache:Redis:SyncTimeout"] = "00:00:00.250",
            ["Cache:Redis:MaxValueBytes"] = "2048",
        });

        var options = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;

        options.ConnectTimeout.ShouldBe(TimeSpan.FromSeconds(2));
        options.SyncTimeout.ShouldBe(TimeSpan.FromMilliseconds(250));
        options.MaxValueBytes.ShouldBe(2048);
    }

    [Fact]
    public async Task Register_AddsReadyHealthCheckThatIsDegradedOnFailure()
    {
        await using var provider = Build(new() { ["Cache:Redis:ConnectionString"] = "127.0.0.1:1" });

        var registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(r => r.Name == RedisHealthCheck.Name);

        registration.Tags.ShouldBe(["ready"]);
        registration.FailureStatus.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task Register_MissingConnectionString_ValidationNamesKey()
    {
        await using var provider = Build([]);

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value);

        exception.Message.ShouldContain("Cache:Redis:ConnectionString");
    }

    [Theory]
    [InlineData("Cache:Redis:ConnectTimeout", "soon")]
    [InlineData("Cache:Redis:ConnectTimeout", "00:00:00")]
    [InlineData("Cache:Redis:SyncTimeout", "-00:00:01")]
    [InlineData("Cache:Redis:SyncTimeout", "00:05:00")]
    [InlineData("Cache:Redis:MaxValueBytes", "0")]
    [InlineData("Cache:Redis:MaxValueBytes", "big")]
    public async Task Register_InvalidSetting_ValidationNamesKey(string key, string value)
    {
        await using var provider = Build(new() { ["Cache:Redis:ConnectionString"] = "127.0.0.1:1", [key] = value });

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value);

        exception.Message.ShouldContain(key);
    }

    [Fact]
    public async Task Register_UnparsableConnectionString_ValidationNeverEchoesValue()
    {
        await using var provider = Build(new() { ["Cache:Redis:ConnectionString"] = $"password={Password},unknownoption=1" });

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value);

        exception.Message.ShouldContain("Cache:Redis:ConnectionString");
        exception.Message.ShouldNotContain(Password);
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new RedisCacheRegistration().Register(services, configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
