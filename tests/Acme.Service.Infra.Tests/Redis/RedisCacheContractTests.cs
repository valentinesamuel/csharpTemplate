using System.Text;
using Acme.Service.BuildingBlocks.Caching;
using Acme.Service.BuildingBlocks.Infra.Redis;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing.Contracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using StackExchange.Redis;

namespace Acme.Service.Infra.Tests.Redis;

[Trait("Category", "Integration")]
public sealed class RedisCacheContractTests(RedisFixture redis) : CacheContractTests, IClassFixture<RedisFixture>
{
    private RedisCache Subject => (RedisCache)Cache;

    protected override ValueTask<ICache> CreateCacheAsync() =>
        ValueTask.FromResult<ICache>(CreateCache(new RedisCacheOptions { ConnectionString = redis.ConnectionString }));

    [Fact]
    public void KeyPrefix_IsServiceToken() => RedisCache.KeyPrefix.ShouldBe("acme-service:");

    [Fact]
    public async Task SetAsync_Key_IsStoredUnderServicePrefixAsJson()
    {
        var key = NewKey();

        await Cache.SetAsync(key, new CacheContractValue("n", 1, [2]), TimeSpan.FromMinutes(1), Ct);

        await using var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var db = raw.GetDatabase();
        var stored = await db.StringGetAsync(RedisCache.KeyPrefix + key);
        stored.IsNull.ShouldBeFalse();
        ((string)stored!).ShouldBe("""{"Name":"n","Count":1,"Items":[2]}""");
        (await db.KeyExistsAsync(key)).ShouldBeFalse();
        var ttl = await db.KeyTimeToLiveAsync(RedisCache.KeyPrefix + key);
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task SetAndTryAdd_ValueOverMaxSize_ThrowArgumentException()
    {
        await using var cache = CreateCache(new RedisCacheOptions { ConnectionString = redis.ConnectionString, MaxValueBytes = 16 });
        var big = new string('x', 64);

        await Should.ThrowAsync<ArgumentException>(() => cache.SetAsync(NewKey(), big, TimeSpan.FromMinutes(1), Ct));
        await Should.ThrowAsync<ArgumentException>(() => cache.TryAddAsync(NewKey(), big, TimeSpan.FromMinutes(1), Ct));
        (await cache.TryAddAsync(NewKey(), "small", TimeSpan.FromMinutes(1), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task GetAsync_StoredValueNotDeserializable_ReturnsMiss()
    {
        var key = NewKey();
        await using (var raw = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString))
        {
            await raw.GetDatabase().StringSetAsync(RedisCache.KeyPrefix + key, Encoding.UTF8.GetBytes("not json"), TimeSpan.FromMinutes(1));
        }

        (await Cache.GetAsync<CacheContractValue>(key, Ct)).ShouldBeNull();
        Cache.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task HealthCheck_RedisUp_ReturnsHealthy()
    {
        var check = new RedisHealthCheck(Subject);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Operations_Success_KeepBreakerClosed()
    {
        await Cache.SetAsync(NewKey(), 1, TimeSpan.FromMinutes(1), Ct);

        Subject.Breaker.State.ShouldBe(CircuitState.Closed);
        Cache.IsAvailable.ShouldBeTrue();
    }

    private static RedisCache CreateCache(RedisCacheOptions options) =>
        new(options, new RedisCircuitBreaker(TimeProvider.System), TimeProvider.System, NullLogger<RedisCache>.Instance);
}
