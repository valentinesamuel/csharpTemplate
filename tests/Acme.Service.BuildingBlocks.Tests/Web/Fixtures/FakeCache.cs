using System.Text.Json;
using Acme.Service.BuildingBlocks.Caching;

namespace Acme.Service.BuildingBlocks.Tests.Web.Fixtures;

/// <summary>
/// In-process <see cref="ICache"/> that round-trips values through System.Text.Json, like a real provider,
/// with switches to simulate an unavailable store. TTLs are recorded but not enforced.
/// </summary>
internal sealed class FakeCache : ICache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    public bool IsAvailable { get; set; } = true;

    public bool ThrowOnTryAdd { get; set; }

    public List<(string Key, TimeSpan Ttl)> Writes { get; } = [];

    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Keys];
            }
        }
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default);
        }
    }

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _entries[key] = JsonSerializer.Serialize(value);
            Writes.Add((key, ttl));
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryAddAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (ThrowOnTryAdd)
        {
            throw new InvalidOperationException("Simulated cache outage.");
        }

        lock (_gate)
        {
            if (!_entries.TryAdd(key, JsonSerializer.Serialize(value)))
            {
                return Task.FromResult(false);
            }

            Writes.Add((key, ttl));
            return Task.FromResult(true);
        }
    }
}
