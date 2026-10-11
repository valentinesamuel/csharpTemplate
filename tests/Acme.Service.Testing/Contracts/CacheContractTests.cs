using System.Diagnostics;
using Acme.Service.BuildingBlocks.Caching;
using Shouldly;
using Xunit;

namespace Acme.Service.Testing.Contracts;

/// <summary>
/// Behaviour every <see cref="ICache"/> provider must have (P7; reused by Redis in P8). Derive, implement
/// <see cref="CreateCacheAsync"/>, and add the provider's own traits (for example <c>Category=Integration</c>).
/// Keys are unique per test, so a shared backing store is fine.
/// </summary>
public abstract class CacheContractTests : IAsyncLifetime
{
    private ICache? _cache;

    /// <summary>The cache under test (created per test).</summary>
    protected ICache Cache => _cache ?? throw new InvalidOperationException("The cache is not initialized.");

    /// <summary>A short TTL used by the expiry tests. Providers with coarse expiry may raise it.</summary>
    protected virtual TimeSpan ShortTtl => TimeSpan.FromMilliseconds(300);

    /// <summary>How long the expiry tests wait at most for an entry to disappear.</summary>
    protected virtual TimeSpan ExpiryTimeout => TimeSpan.FromSeconds(10);

    /// <summary>Creates the cache under test.</summary>
    protected abstract ValueTask<ICache> CreateCacheAsync();

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync() => _cache = await CreateCacheAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        switch (_cache)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void IsAvailable_HealthyStore_ReturnsTrue() => Cache.IsAvailable.ShouldBeTrue();

    [Fact]
    public async Task GetAsync_MissingKey_ReturnsDefault()
    {
        (await Cache.GetAsync<CacheContractValue>(NewKey(), Ct)).ShouldBeNull();
        (await Cache.GetAsync<int>(NewKey(), Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task SetAsync_ThenGet_RoundTripsValue()
    {
        var key = NewKey();
        var value = new CacheContractValue("name", 42, [1, 2, 3]);

        await Cache.SetAsync(key, value, TimeSpan.FromMinutes(1), Ct);

        var read = await Cache.GetAsync<CacheContractValue>(key, Ct);
        read.ShouldNotBeNull();
        read.Name.ShouldBe("name");
        read.Count.ShouldBe(42);
        read.Items.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task SetAsync_Primitives_RoundTrip()
    {
        var intKey = NewKey();
        var stringKey = NewKey();

        await Cache.SetAsync(intKey, 7, TimeSpan.FromMinutes(1), Ct);
        await Cache.SetAsync(stringKey, "text", TimeSpan.FromMinutes(1), Ct);

        (await Cache.GetAsync<int>(intKey, Ct)).ShouldBe(7);
        (await Cache.GetAsync<string>(stringKey, Ct)).ShouldBe("text");
    }

    [Fact]
    public async Task SetAsync_ExistingKey_Overwrites()
    {
        var key = NewKey();

        await Cache.SetAsync(key, "first", TimeSpan.FromMinutes(1), Ct);
        await Cache.SetAsync(key, "second", TimeSpan.FromMinutes(1), Ct);

        (await Cache.GetAsync<string>(key, Ct)).ShouldBe("second");
    }

    [Fact]
    public async Task SetAsync_TtlElapsed_EntryExpires()
    {
        var key = NewKey();

        await Cache.SetAsync(key, "value", ShortTtl, Ct);

        (await WaitUntilMissingAsync(key)).ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveAsync_ExistingKey_RemovesEntry()
    {
        var key = NewKey();
        await Cache.SetAsync(key, "value", TimeSpan.FromMinutes(1), Ct);

        await Cache.RemoveAsync(key, Ct);

        (await Cache.GetAsync<string>(key, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task RemoveAsync_MissingKey_DoesNotThrow() =>
        await Should.NotThrowAsync(() => Cache.RemoveAsync(NewKey(), Ct));

    [Fact]
    public async Task TryAddAsync_MissingKey_AddsAndReturnsTrue()
    {
        var key = NewKey();

        (await Cache.TryAddAsync(key, "value", TimeSpan.FromMinutes(1), Ct)).ShouldBeTrue();

        (await Cache.GetAsync<string>(key, Ct)).ShouldBe("value");
    }

    [Fact]
    public async Task TryAddAsync_ExistingKey_ReturnsFalseAndKeepsValue()
    {
        var key = NewKey();
        await Cache.SetAsync(key, "original", TimeSpan.FromMinutes(1), Ct);

        (await Cache.TryAddAsync(key, "other", TimeSpan.FromMinutes(1), Ct)).ShouldBeFalse();

        (await Cache.GetAsync<string>(key, Ct)).ShouldBe("original");
    }

    [Fact]
    public async Task TryAddAsync_ConcurrentCallers_ExactlyOneWins()
    {
        var key = NewKey();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 32)
            .Select(i => Task.Run(
                async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    return await Cache.TryAddAsync(key, i, TimeSpan.FromMinutes(1), Ct).ConfigureAwait(false);
                },
                Ct))
            .ToList();
        start.SetResult();

        var results = await Task.WhenAll(attempts);

        results.Count(added => added).ShouldBe(1);
    }

    [Fact]
    public async Task TryAddAsync_AfterExpiry_AddsAgain()
    {
        var key = NewKey();
        (await Cache.TryAddAsync(key, "first", ShortTtl, Ct)).ShouldBeTrue();

        (await WaitUntilMissingAsync(key)).ShouldBeTrue();

        (await Cache.TryAddAsync(key, "second", TimeSpan.FromMinutes(1), Ct)).ShouldBeTrue();
        (await Cache.GetAsync<string>(key, Ct)).ShouldBe("second");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Operations_BlankKey_ThrowArgumentException(string key)
    {
        await Should.ThrowAsync<ArgumentException>(() => Cache.GetAsync<string>(key, Ct));
        await Should.ThrowAsync<ArgumentException>(() => Cache.SetAsync(key, "v", TimeSpan.FromMinutes(1), Ct));
        await Should.ThrowAsync<ArgumentException>(() => Cache.RemoveAsync(key, Ct));
        await Should.ThrowAsync<ArgumentException>(() => Cache.TryAddAsync(key, "v", TimeSpan.FromMinutes(1), Ct));
    }

    [Fact]
    public async Task SetAndTryAdd_NonPositiveTtl_ThrowArgumentOutOfRange()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Cache.SetAsync(NewKey(), "v", TimeSpan.Zero, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Cache.TryAddAsync(NewKey(), "v", TimeSpan.FromSeconds(-1), Ct));
    }

    /// <summary>The current test's cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A key unique to this test run.</summary>
    protected static string NewKey() => "contract:" + Guid.NewGuid().ToString("N");

    private async Task<bool> WaitUntilMissingAsync(string key)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < ExpiryTimeout)
        {
            if (await Cache.GetAsync<string>(key, Ct).ConfigureAwait(false) is null)
            {
                return true;
            }

            await Task.Delay(50, Ct).ConfigureAwait(false);
        }

        return false;
    }
}

/// <summary>A composite cache value used by the contract tests.</summary>
/// <param name="Name">Text.</param>
/// <param name="Count">Number.</param>
/// <param name="Items">List.</param>
public sealed record CacheContractValue(string Name, int Count, IReadOnlyList<int> Items);
