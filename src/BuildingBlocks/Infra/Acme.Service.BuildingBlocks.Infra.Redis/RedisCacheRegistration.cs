using Acme.Service.BuildingBlocks.Caching;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.Redis;

/// <summary>
/// <c>Cache:Provider=Redis</c>: a distributed <see cref="ICache"/> on Redis. Requires
/// <c>Cache:Redis:ConnectionString</c>; optional <c>Cache:Redis:ConnectTimeout</c>, <c>Cache:Redis:SyncTimeout</c>
/// (both default 00:00:00.500) and <c>Cache:Redis:MaxValueBytes</c> (default 1048576). Options are validated at
/// startup. Adds the <c>redis</c> readiness check (tag <c>ready</c>, Degraded when Redis is down). Startup never
/// waits for Redis.
/// </summary>
public sealed class RedisCacheRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "Redis";

    /// <summary>Health check tag of the readiness endpoint.</summary>
    public const string ReadyTag = "ready";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Cache;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RedisCacheOptions>()
            .Configure(o => RedisCacheOptions.Read(configuration, o))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RedisCacheOptions>, RedisCacheOptions.Validator>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RedisCache>();
        services.AddSingleton<ICache>(sp => sp.GetRequiredService<RedisCache>());

        services.AddHealthChecks()
            .AddCheck<RedisHealthCheck>(RedisHealthCheck.Name, failureStatus: HealthStatus.Degraded, tags: [ReadyTag]);
    }
}
