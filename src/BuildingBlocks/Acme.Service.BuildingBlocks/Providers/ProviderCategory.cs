namespace Acme.Service.BuildingBlocks.Providers;

/// <summary>Infrastructure port families selectable via <c>&lt;Category&gt;:Provider</c> (D-02).</summary>
public enum ProviderCategory
{
    /// <summary><c>Cache:Provider</c>.</summary>
    Cache,

    /// <summary><c>Blob:Provider</c>.</summary>
    Blob,

    /// <summary><c>Notifications:Provider</c>.</summary>
    Notifications,

    /// <summary><c>Jobs:Provider</c>.</summary>
    Jobs,

    /// <summary><c>Locks:Provider</c>.</summary>
    Locks,
}
