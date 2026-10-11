using Acme.Service.BuildingBlocks.Locking;

namespace Acme.Service.BuildingBlocks.Infra.InMemory;

/// <summary>
/// Process-local <see cref="IDistributedLock"/> (<c>Locks:Provider=InMemory</c>, rejected in Production by D-21).
/// It excludes callers within one process only, so it is for development and tests. Names are compared ordinally;
/// per-name state is dropped once nobody holds or waits for the lock.
/// </summary>
internal sealed class InMemoryDistributedLock : IDistributedLock
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Number of names with a holder or waiter (for tests).</summary>
    internal int TrackedNameCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var entry = Retain(name);
        var acquired = false;
        try
        {
            acquired = await entry.Semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return acquired ? new Handle(this, name, entry) : null;
        }
        finally
        {
            if (!acquired)
            {
                Release(name, entry);
            }
        }
    }

    private Entry Retain(string name)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                entry = new Entry();
                _entries[name] = entry;
            }

            entry.References++;
            return entry;
        }
    }

    private void Release(string name, Entry entry)
    {
        lock (_gate)
        {
            entry.References--;
            if (entry.References == 0)
            {
                _entries.Remove(name);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }
    }

    /// <summary>Releases the lock once; further disposals are no-ops.</summary>
    private sealed class Handle(InMemoryDistributedLock owner, string name, Entry entry) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                entry.Semaphore.Release();
                owner.Release(name, entry);
            }

            return ValueTask.CompletedTask;
        }
    }
}
