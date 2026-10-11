namespace Acme.Service.BuildingBlocks.Locking;

/// <summary>Provider-neutral distributed lock port (selected by <c>Locks:Provider</c>; D-06).</summary>
public interface IDistributedLock
{
    /// <summary>
    /// Tries to acquire the lock <paramref name="name"/>, waiting at most <paramref name="timeout"/>
    /// (<see cref="TimeSpan.Zero"/> to try once). Returns a handle that releases the lock when disposed,
    /// or null when the lock could not be acquired in time.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan timeout, CancellationToken cancellationToken);
}
