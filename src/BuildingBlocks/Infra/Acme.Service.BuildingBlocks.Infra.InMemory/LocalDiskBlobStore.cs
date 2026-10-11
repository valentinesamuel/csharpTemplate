using Acme.Service.BuildingBlocks.Blob;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.InMemory;

/// <summary>
/// Development <see cref="IBlobStore"/> on the local file system (<c>Blob:Provider=Local</c>, rejected in
/// Production by D-21). Blobs live under <c>Blob:Local:RootPath</c>; each key segment is a directory or file name.
/// </summary>
/// <remarks>
/// Path traversal is blocked in depth: the key must be a valid <see cref="BlobKey"/>, the combined path is
/// canonicalised and must stay strictly under the root, and no existing directory or file below the root may be a
/// symbolic link (so a link cannot redirect a write outside the root). Writes go to a temporary file in the target
/// directory and are moved into place, so readers never see partial content. Content types are not stored, and
/// <see cref="GetDownloadUrlAsync"/> returns null (callers stream through <see cref="OpenReadAsync"/>).
/// A key that is used as a file cannot also be a directory prefix of another key (and vice versa).
/// </remarks>
internal sealed class LocalDiskBlobStore : IBlobStore
{
    private const int BufferSize = 81920;

    // '~' is not a valid BlobKey character, so temporary files can never collide with a blob.
    private const string TempSuffix = ".tmp~";

    private readonly string _root;

    public LocalDiskBlobStore(IOptions<LocalBlobOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var rootPath = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException($"{LocalBlobOptions.RootPathKey} must be an absolute path.", nameof(options));
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        Directory.CreateDirectory(_root);
    }

    /// <summary>The canonical absolute root directory.</summary>
    internal string RootPath => _root;

    /// <inheritdoc />
    public async Task PutAsync(BlobKey key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);

        var path = ResolvePath(key.Value);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        // Re-check after creating directories: a link could have been planted between the checks.
        path = ResolvePath(key.Value);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        try
        {
            var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            await using (target.ConfigureAwait(false))
            {
                await content.CopyToAsync(target, BufferSize, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(BlobKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        var path = ResolvePath(key.Value);
        try
        {
            Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, useAsync: true);
            return Task.FromResult<Stream?>(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(BlobKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        var path = ResolvePath(key.Value);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(BlobKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(File.Exists(ResolvePath(key.Value)));
    }

    /// <inheritdoc />
    public Task<Uri?> GetDownloadUrlAsync(BlobKey key, TimeSpan expiresIn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<Uri?>(null);
    }

    /// <summary>
    /// Maps <paramref name="key"/> to an absolute path strictly under the root. Validates the raw text again
    /// (defence in depth; callers normally pass a <see cref="BlobKey"/>), canonicalises the result and rejects any
    /// symbolic link below the root.
    /// </summary>
    /// <exception cref="ArgumentException">The key is invalid or resolves outside the root.</exception>
    internal string ResolvePath(string key)
    {
        if (!BlobKey.TryCreate(key, out var validated))
        {
            throw new ArgumentException("Invalid blob key.", nameof(key));
        }

        var segments = validated.Value.Split('/');
        var path = Path.GetFullPath(Path.Join([_root, .. segments]));

        var rootWithSeparator = _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException("The blob key resolves outside the storage root.", nameof(key));
        }

        // Every existing component below the root must be a real directory or file, never a link (including a
        // dangling one, which a write could otherwise follow).
        var current = _root;
        foreach (var segment in segments)
        {
            current = Path.Join(current, segment);
            if (new FileInfo(current).LinkTarget is not null)
            {
                throw new ArgumentException("The blob key resolves through a symbolic link.", nameof(key));
            }

            if (!Directory.Exists(current))
            {
                break;
            }
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temporary file is ignored by every operation.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}

/// <summary>Settings of <see cref="LocalDiskBlobStore"/> (section <c>Blob:Local</c>).</summary>
internal sealed class LocalBlobOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Blob:Local";

    /// <summary>Full configuration key of <see cref="RootPath"/>.</summary>
    public const string RootPathKey = SectionName + ":RootPath";

    /// <summary>Absolute root directory (relative configured paths are resolved against the content root).</summary>
    public string? RootPath { get; set; }
}
