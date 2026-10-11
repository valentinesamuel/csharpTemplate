namespace Acme.Service.BuildingBlocks.Caching;

/// <summary>
/// Provider-neutral cache port (selected by <c>Cache:Provider</c>). Callers must tolerate the cache
/// being unavailable; it is never a source of truth.
/// </summary>
public interface ICache
{
    /// <summary>True when the backing store is currently reachable.</summary>
    bool IsAvailable { get; }

    /// <summary>Returns the cached value, or <c>default</c> when the key is missing or expired.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, overwriting any existing entry.</summary>
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken);

    /// <summary>Removes the entry, if any.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken);

    /// <summary>Atomically stores the value only when the key does not exist. Returns true when it was added.</summary>
    Task<bool> TryAddAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken);
}
