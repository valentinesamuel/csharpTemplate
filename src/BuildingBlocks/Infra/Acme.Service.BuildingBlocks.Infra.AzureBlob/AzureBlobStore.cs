using System.Net;
using Acme.Service.BuildingBlocks.Blob;
using Azure;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.AzureBlob;

/// <summary>
/// <see cref="IBlobStore"/> on Azure Blob Storage (<c>Blob:Provider=AzureBlob</c>; Azurite in development and tests).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Client.</b> One <see cref="BlobServiceClient"/> per process (a DI singleton built by
/// <see cref="CreateClient"/>): shared key from <c>Blob:AzureBlob:ConnectionString</c>, or
/// <c>DefaultAzureCredential</c> for <c>Blob:AzureBlob:ServiceUri</c>. Transient failures (5xx, throttling, network
/// errors) are retried by the Azure SDK default retry policy (exponential back-off); no extra retry is layered on
/// top.</item>
/// <item><b>Container.</b> Checked once, at first use. A missing container throws an
/// <see cref="InvalidOperationException"/> naming <c>Blob:AzureBlob:Container</c>, unless
/// <c>Blob:AzureBlob:CreateIfMissing=true</c> (development), in which case it is created. A 403 on the check (an
/// identity without container read access) is treated as "exists", and later operations report their own errors. A
/// container deleted while running is reported the same way on the next call.</item>
/// <item><b>Uploads</b> stream through the SDK's partitioned uploader with explicit <see cref="UploadTransferOptions"/>:
/// seekable content is read straight from the caller's stream; non-seekable content is buffered in 8 MiB blocks with
/// at most 4 in flight (about 32 MiB), never as a whole file. (The SDK default would buffer up to 256 MiB of a
/// non-seekable stream before its first request.) The caller's stream is not closed.</item>
/// <item><b>Download URLs</b> are read-only SAS URLs for one blob: a service SAS signed with the account key
/// (connection string), or a user delegation SAS (service URI with Entra ID). The lifetime must be positive and is
/// capped at <see cref="MaxDownloadUrlLifetime"/> (15 minutes). User delegation keys are cached for up to an hour.</item>
/// <item><b>Keys</b> are always <see cref="BlobKey"/> values (traversal rejected in core) and are used verbatim as
/// blob names; no path is ever built from raw text.</item>
/// </list>
/// The connection string and account key are never logged or placed in exception messages (INV-11, INV-28).
/// </remarks>
internal sealed partial class AzureBlobStore : IBlobStore, IDisposable
{
    /// <summary>The longest lifetime of a download URL; longer requests are capped.</summary>
    internal static readonly TimeSpan MaxDownloadUrlLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Validity of a requested user delegation key (the service allows up to seven days).</summary>
    internal static readonly TimeSpan DelegationKeyLifetime = TimeSpan.FromHours(1);

    /// <summary>Back-dating of a delegation key's start, for clock skew between this host and the service.</summary>
    private static readonly TimeSpan _clockSkew = TimeSpan.FromMinutes(5);

    private const string DefaultContentType = "application/octet-stream";

    private const int BlockSize = 8 * 1024 * 1024;

    /// <summary>
    /// Upload partitioning: single request up to one block, then blocks of <c>8 MiB</c>, at most 4 in parallel. Bounds
    /// the memory used for non-seekable streams to roughly 32 MiB per upload.
    /// </summary>
    internal static readonly StorageTransferOptions UploadTransferOptions = new()
    {
        InitialTransferSize = BlockSize,
        MaximumTransferSize = BlockSize,
        MaximumConcurrency = 4,
    };

    private readonly BlobServiceClient _service;
    private readonly BlobContainerClient _container;
    private readonly bool _createIfMissing;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AzureBlobStore> _logger;
    private readonly SemaphoreSlim _containerGate = new(1, 1);
    private readonly SemaphoreSlim _delegationKeyGate = new(1, 1);
    private volatile bool _containerVerified;
    private UserDelegationKey? _delegationKey;

    public AzureBlobStore(BlobServiceClient service, IOptions<AzureBlobOptions> options, TimeProvider timeProvider, ILogger<AzureBlobStore> logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = options.Value;
        var container = settings.Container
            ?? throw new ArgumentException($"{AzureBlobOptions.ContainerKey} is required.", nameof(options));

        _service = service;
        _container = service.GetBlobContainerClient(container);
        _createIfMissing = settings.CreateIfMissing;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Builds the service client: shared key from the connection string, otherwise <c>DefaultAzureCredential</c>.</summary>
    internal static BlobServiceClient CreateClient(AzureBlobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ConnectionString is { } connectionString)
        {
            return new BlobServiceClient(connectionString);
        }

        var serviceUri = options.ParsedServiceUri
            ?? throw new ArgumentException($"{AzureBlobOptions.ServiceUriKey} must be an absolute http or https URL.", nameof(options));
        return new BlobServiceClient(serviceUri, new DefaultAzureCredential());
    }

    /// <inheritdoc />
    public async Task PutAsync(BlobKey key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        var options = new BlobUploadOptions
        {
            TransferOptions = UploadTransferOptions,
            HttpHeaders = new BlobHttpHeaders { ContentType = string.IsNullOrWhiteSpace(contentType) ? DefaultContentType : contentType },
        };

        try
        {
            // No conditions: an existing blob is overwritten.
            await _container.GetBlobClient(key.Value).UploadAsync(content, options, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
        {
            throw ContainerMissing(ex);
        }
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenReadAsync(BlobKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await _container.GetBlobClient(key.Value).DownloadStreamingAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Disposing the content stream releases the HTTP response.
            return response.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
        {
            return null;
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
        {
            throw ContainerMissing(ex);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(BlobKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _container.GetBlobClient(key.Value).DeleteAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
        {
            // Deleting a missing blob is not an error.
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
        {
            throw ContainerMissing(ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(BlobKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _container.GetBlobClient(key.Value).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
        {
            return false;
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
        {
            throw ContainerMissing(ex);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expiresIn"/> is zero or negative.</exception>
    /// <remarks>
    /// Returns a read-only SAS URL for the blob valid for <paramref name="expiresIn"/>, capped at 15 minutes. The
    /// blob is not checked for existence.
    /// </remarks>
    public async Task<Uri?> GetDownloadUrlAsync(BlobKey key, TimeSpan expiresIn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var lifetime = ClampLifetime(expiresIn);
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        var blob = _container.GetBlobClient(key.Value);
        var expiresOn = _timeProvider.GetUtcNow().Add(lifetime);
        var sas = CreateReadSas(_container.Name, key.Value, expiresOn);

        if (blob.CanGenerateSasUri)
        {
            // Shared key credential (connection string): a service SAS.
            return blob.GenerateSasUri(sas);
        }

        var delegationKey = await GetDelegationKeyAsync(expiresOn, cancellationToken).ConfigureAwait(false);
        return blob.GenerateUserDelegationSasUri(sas, delegationKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _containerGate.Dispose();
        _delegationKeyGate.Dispose();
    }

    /// <summary>Validates and caps a download URL lifetime (positive, at most <see cref="MaxDownloadUrlLifetime"/>).</summary>
    internal static TimeSpan ClampLifetime(TimeSpan expiresIn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresIn, TimeSpan.Zero);
        return expiresIn > MaxDownloadUrlLifetime ? MaxDownloadUrlLifetime : expiresIn;
    }

    /// <summary>A read-only SAS definition for one blob.</summary>
    internal static BlobSasBuilder CreateReadSas(string container, string blobName, DateTimeOffset expiresOn) =>
        new(BlobSasPermissions.Read, expiresOn)
        {
            BlobContainerName = container,
            BlobName = blobName,
            Resource = "b",
        };

    /// <summary>
    /// Returns a cached user delegation key valid until at least <paramref name="sasExpiresOn"/>, requesting a new
    /// one (valid for <see cref="DelegationKeyLifetime"/>) when needed. A SAS must not outlive its key.
    /// </summary>
    private async Task<UserDelegationKey> GetDelegationKeyAsync(DateTimeOffset sasExpiresOn, CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _delegationKey);
        if (cached is not null && cached.SignedExpiresOn >= sasExpiresOn)
        {
            return cached;
        }

        await _delegationKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _delegationKey;
            if (cached is not null && cached.SignedExpiresOn >= sasExpiresOn)
            {
                return cached;
            }

            var now = _timeProvider.GetUtcNow();
            var response = await _service.GetUserDelegationKeyAsync(now - _clockSkew, now + DelegationKeyLifetime, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _delegationKey, response.Value);
            return response.Value;
        }
        finally
        {
            _delegationKeyGate.Release();
        }
    }

    /// <summary>Verifies (or, with CreateIfMissing, creates) the container once; a failure is retried on the next call.</summary>
    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_containerVerified)
        {
            return;
        }

        await _containerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_containerVerified)
            {
                return;
            }

            if (!await ContainerExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_createIfMissing)
                {
                    throw ContainerMissing(null);
                }

                var created = await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (created is not null)
                {
                    LogContainerCreated(_logger, _container.Name);
                }
            }

            _containerVerified = true;
        }
        finally
        {
            _containerGate.Release();
        }
    }

    private async Task<bool> ContainerExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _container.ExistsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Forbidden)
        {
            LogContainerNotVerifiable(_logger, _container.Name);
            return true;
        }
    }

    private InvalidOperationException ContainerMissing(Exception? inner)
    {
        _containerVerified = false;
        return new InvalidOperationException(
            $"The Azure Blob container '{_container.Name}' configured in {AzureBlobOptions.ContainerKey} does not exist. Create it, or set " +
            $"{AzureBlobOptions.CreateIfMissingKey}=true to create it at first use (development only).",
            inner);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created Azure Blob container {Container} (Blob:AzureBlob:CreateIfMissing=true)")]
    private static partial void LogContainerCreated(ILogger logger, string container);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Access to Azure Blob container {Container} could not be verified (403); continuing, operations report their own errors")]
    private static partial void LogContainerNotVerifiable(ILogger logger, string container);
}
