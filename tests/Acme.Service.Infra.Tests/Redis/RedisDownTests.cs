using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Acme.Service.BuildingBlocks.Infra.Redis;
using Acme.Service.Infra.Tests.Fixtures;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Acme.Service.Infra.Tests.Redis;

/// <summary>
/// Redis-down behaviour (P8): the cache points at a closed local port. No Docker needed, so these run in the
/// unit suite.
/// </summary>
public sealed class RedisDownTests : IAsyncDisposable
{
    private const string Password = "redis-down-secret-7f3a";

    private readonly RecordingLoggerProvider _logs = new();
    private readonly ManualTimeProvider _time = new();
    private readonly RedisCache _cache;

    public RedisDownTests()
    {
        var options = new RedisCacheOptions { ConnectionString = $"127.0.0.1:{ClosedPort()},password={Password}" };
        _cache = new RedisCache(options, new RedisCircuitBreaker(_time), _time, _logs.CreateLogger<RedisCache>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetAsync_RedisDown_ReturnsMissWithinOneSecond()
    {
        var first = Stopwatch.StartNew();
        var value = await _cache.GetAsync<string>("key", Ct);
        first.Stop();

        var second = Stopwatch.StartNew();
        var number = await _cache.GetAsync<int>("key", Ct);
        second.Stop();

        first.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        second.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        value.ShouldBeNull();
        number.ShouldBe(0);
    }

    [Fact]
    public async Task TryAddAsync_RedisDown_ThrowsCacheUnavailableException()
    {
        var elapsed = Stopwatch.StartNew();

        await Should.ThrowAsync<CacheUnavailableException>(() => _cache.TryAddAsync("key", "v", TimeSpan.FromMinutes(1), Ct));

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SetAndRemove_RedisDown_AreSwallowed()
    {
        await Should.NotThrowAsync(() => _cache.SetAsync("key", "v", TimeSpan.FromMinutes(1), Ct));
        await Should.NotThrowAsync(() => _cache.RemoveAsync("key", Ct));
    }

    [Fact]
    public async Task IsAvailable_AfterFailedCall_ReturnsFalse()
    {
        await _cache.GetAsync<string>("key", Ct);

        _cache.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task Breaker_FiveFailures_OpensAndSkipsRedis()
    {
        for (var i = 0; i < 5; i++)
        {
            await _cache.GetAsync<string>("key", Ct);
        }

        _cache.Breaker.State.ShouldBe(CircuitState.Open);
        _cache.IsAvailable.ShouldBeFalse();
        _logs.Entries.Count(e => e.Message.Contains("circuit breaker opened", StringComparison.Ordinal)).ShouldBe(1);

        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            (await _cache.GetAsync<string>("key", Ct)).ShouldBeNull();
        }

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500));
        _cache.Breaker.State.ShouldBe(CircuitState.Open);
        var exception = await Should.ThrowAsync<CacheUnavailableException>(() => _cache.TryAddAsync("key", "v", TimeSpan.FromMinutes(1), Ct));
        exception.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task Breaker_AfterBreakWithRedisStillDown_TrialFailsAndReopens()
    {
        await FailUntilOpenAsync();

        _time.Advance(TimeSpan.FromSeconds(30));
        _cache.Breaker.State.ShouldBe(CircuitState.HalfOpen);

        (await _cache.GetAsync<string>("key", Ct)).ShouldBeNull();

        _cache.Breaker.State.ShouldBe(CircuitState.Open);
    }

    [Fact]
    public async Task HealthCheck_RedisDown_ReturnsDegraded()
    {
        var check = new RedisHealthCheck(_cache);

        (await check.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Degraded);

        await FailUntilOpenAsync();
        var open = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        open.Status.ShouldBe(HealthStatus.Degraded);
        open.Description.ShouldNotBeNull();
        open.Description.ShouldNotContain(Password);
    }

    [Fact]
    public async Task Warnings_RepeatedFailures_AreRateLimitedToOnePerMinute()
    {
        for (var i = 0; i < 20; i++)
        {
            await _cache.SetAsync("key", i, TimeSpan.FromMinutes(1), Ct);
        }

        UnavailableWarnings().Count.ShouldBe(1);

        _time.Advance(RedisCache.WarningInterval);
        await _cache.RemoveAsync("key", Ct);

        var warnings = UnavailableWarnings();
        warnings.Count.ShouldBe(2);
        warnings[1].Message.ShouldContain("19 similar warnings suppressed");
    }

    [Fact]
    public async Task Logs_RedisDown_NeverContainPassword()
    {
        await FailUntilOpenAsync();
        _time.Advance(RedisCache.WarningInterval);
        await _cache.SetAsync("key", 1, TimeSpan.FromMinutes(1), Ct);
        await Should.ThrowAsync<CacheUnavailableException>(() => _cache.TryAddAsync("key", 1, TimeSpan.FromMinutes(1), Ct));

        _logs.Entries.ShouldNotBeEmpty();
        foreach (var entry in _logs.Entries)
        {
            entry.Message.ShouldNotContain(Password);
            entry.Exception?.ToString().ShouldNotContain(Password);
        }
    }

    [Fact]
    public async Task Operations_CallerCancelled_ThrowAndDoNotCountAsFailure()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _cache.GetAsync<string>("key", cancelled.Token));

        _cache.Breaker.State.ShouldBe(CircuitState.Closed);
    }

    public async ValueTask DisposeAsync()
    {
        await _cache.DisposeAsync();
        _logs.Dispose();
    }

    private async Task FailUntilOpenAsync()
    {
        while (_cache.Breaker.State != CircuitState.Open)
        {
            await _cache.GetAsync<string>("key", Ct);
        }
    }

    private List<LogEntry> UnavailableWarnings() =>
        [.. _logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.StartsWith("Redis cache unavailable", StringComparison.Ordinal))];

    private static int ClosedPort()
    {
        // Bind an ephemeral port, then release it: nothing listens there afterwards (connection refused).
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
