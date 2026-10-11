namespace Acme.Service.BuildingBlocks.Blob;

/// <summary>Provider-neutral blob storage port (selected by <c>Blob:Provider</c>).</summary>
public interface IBlobStore
{
    /// <summary>Writes <paramref name="content"/> under <paramref name="key"/>, overwriting any existing blob.</summary>
    Task PutAsync(BlobKey key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>Opens the blob for reading, or returns null when it does not exist. The caller disposes the stream.</summary>
    Task<Stream?> OpenReadAsync(BlobKey key, CancellationToken cancellationToken);

    /// <summary>Deletes the blob. Deleting a missing blob is not an error.</summary>
    Task DeleteAsync(BlobKey key, CancellationToken cancellationToken);

    /// <summary>True when the blob exists.</summary>
    Task<bool> ExistsAsync(BlobKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a time-limited download URL, or null when the provider does not support it
    /// (callers then stream through <see cref="OpenReadAsync"/>).
    /// </summary>
    Task<Uri?> GetDownloadUrlAsync(BlobKey key, TimeSpan expiresIn, CancellationToken cancellationToken);
}
