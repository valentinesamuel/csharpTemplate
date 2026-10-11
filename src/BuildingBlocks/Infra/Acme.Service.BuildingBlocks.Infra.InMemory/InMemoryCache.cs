using System.Text.Json;
using Acme.Service.BuildingBlocks.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.InMemory;

/// <summary>
/// Process-local <see cref="ICache"/> (<c>Cache:Provider=InMemory</c>) on a private <see cref="MemoryCache"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Values are stored as System.Text.Json UTF-8 bytes, like the distributed providers, so callers never
/// share mutable instances with the cache and non-serializable values fail here first.</item>
/// <item>Every write runs under one lock, which makes <see cref="TryAddAsync{T}"/> atomic within the process.
/// The cache is per replica: with several replicas, <see cref="TryAddAsync{T}"/> is not a cross-replica guard
/// (D-21 warns when idempotency relies on it).</item>
/// </list>
/// </remarks>
internal sealed class InMemoryCache : ICache, IDisposable
{
    private readonly MemoryCache _cache = new(Options.Create(new MemoryCacheOptions()));
    private readonly Lock _writeGate = new();

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _cache.TryGetValue(key, out byte[]? bytes) && bytes is not null
                ? JsonSerializer.Deserialize<T>(bytes)
                : default);
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        lock (_writeGate)
        {
            _cache.Set(key, bytes, ttl);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_writeGate)
        {
            _cache.Remove(key);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TryAddAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        lock (_writeGate)
        {
            // TryGetValue honours expiration, so an expired entry counts as absent.
            if (_cache.TryGetValue(key, out _))
            {
                return Task.FromResult(false);
            }

            _cache.Set(key, bytes, ttl);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cache.Dispose();
}
