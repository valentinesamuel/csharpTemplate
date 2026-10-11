using System.Globalization;
using System.Net;
using Acme.Service.BuildingBlocks.Blob;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.S3;

/// <summary>
/// <see cref="IBlobStore"/> on Amazon S3 or an S3-compatible store (<c>Blob:Provider=S3</c>; Cloudflare R2, MinIO,
/// RustFS and similar).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Client.</b> One <see cref="IAmazonS3"/> per process (a DI singleton built by <see cref="CreateClient"/>).
/// Request checksum calculation and response checksum validation are "when required" (WH-29), because the AWS SDK
/// defaults send CRC trailers that several S3-compatible stores reject. Transient failures (5xx, throttling, network
/// errors) are retried by the SDK default retry policy; no extra retry is layered on top.</item>
/// <item><b>Bucket.</b> Checked once, at first use, with a HEAD request. A missing bucket throws an
/// <see cref="InvalidOperationException"/> naming <c>Blob:S3:Bucket</c>, unless <c>Blob:S3:CreateIfMissing=true</c>
/// (development), in which case it is created. A 403 on the check (credentials without <c>s3:ListBucket</c>) is
/// treated as "exists", and later operations report their own errors. A bucket deleted while running is reported the
/// same way on the next call.</item>
/// <item><b>Uploads</b> stream through the S3 transfer utility: seekable content under 16 MiB is sent with one PUT
/// straight from the caller's stream; larger or non-seekable content goes as a multipart upload, buffering at most a
/// few parts at a time, never the whole file. The caller's stream is neither closed nor rewound.</item>
/// <item><b>Download URLs</b> are presigned GET URLs. The lifetime must be positive, is rounded down to whole seconds
/// (minimum one) and is capped at <see cref="MaxDownloadUrlLifetime"/> (15 minutes); the signed lifetime is verified
/// against the SDK's clock-skew-corrected clock.</item>
/// <item><b>Keys</b> are always <see cref="BlobKey"/> values (traversal rejected in core) and are used verbatim as
/// object keys; no path is ever built from raw text.</item>
/// </list>
/// Credentials are never logged or placed in exception messages (INV-11, INV-28).
/// </remarks>
internal sealed partial class S3BlobStore : IBlobStore, IDisposable
{
    /// <summary>The longest lifetime of a download URL; longer requests are capped.</summary>
    internal static readonly TimeSpan MaxDownloadUrlLifetime = TimeSpan.FromMinutes(15);

    private const string DefaultContentType = "application/octet-stream";
    private const string NoSuchBucket = "NoSuchBucket";
    private const string NoSuchKey = "NoSuchKey";
    private const string ExpiresParameter = "X-Amz-Expires";
    private const int MaxSigningAttempts = 3;

    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly bool _createIfMissing;
    private readonly Protocol _urlProtocol;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<S3BlobStore> _logger;
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private volatile bool _bucketVerified;

    public S3BlobStore(IAmazonS3 client, IOptions<S3BlobOptions> options, TimeProvider timeProvider, ILogger<S3BlobStore> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = options.Value;
        _client = client;
        _bucket = settings.Bucket ?? throw new ArgumentException($"{S3BlobOptions.BucketKey} is required.", nameof(options));
        _createIfMissing = settings.CreateIfMissing;
        _urlProtocol = settings.ServiceUri?.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Builds the S3 client: path-style addressing and service URL for S3-compatible stores, checksums only when
    /// required (WH-29), static credentials when configured, otherwise the default AWS credential chain.
    /// </summary>
    internal static AmazonS3Client CreateClient(S3BlobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        if (options.ServiceUri is { } serviceUri)
        {
            config.ServiceURL = serviceUri.ToString();
            if (options.Region is { } signingRegion)
            {
                config.AuthenticationRegion = signingRegion;
            }
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return options.HasStaticCredentials
            ? new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
            : new AmazonS3Client(config);
    }

    /// <inheritdoc />
    public async Task PutAsync(BlobKey key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);

        // The wrapper keeps the caller's stream open (the transfer utility disposes non-seekable input streams).
        using var input = new LeaveOpenReadStream(content);
        var request = new TransferUtilityUploadRequest
        {
            BucketName = _bucket,
            Key = key.Value,
            InputStream = input,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? DefaultContentType : contentType,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
        };

        // The transfer utility does not own (or dispose) the shared client.
        using var transfer = new TransferUtility(_client);
        await RunAsync(() => transfer.UploadAsync(request, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenReadAsync(BlobKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key.Value }, cancellationToken)
                .ConfigureAwait(false);

            // Disposing the response stream releases the HTTP response (GetObjectResponse.Dispose does only that).
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == NoSuchKey)
        {
            return null;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == NoSuchBucket)
        {
            throw BucketMissing(ex);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(BlobKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);

        // S3 reports success for a key that does not exist.
        await RunAsync(() => _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key.Value }, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(BlobKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key.Value }, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // HEAD responses carry no error code; the bucket was verified above, so 404 means the key is missing.
            return false;
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expiresIn"/> is zero or negative.</exception>
    /// <remarks>
    /// Returns a presigned GET URL valid for <paramref name="expiresIn"/> in whole seconds (at least one), capped at
    /// 15 minutes. The key is not checked for existence. The SDK turns the absolute expiry into the signed
    /// <c>X-Amz-Expires</c> lifetime using its own clock-skew-corrected clock, so the signed lifetime is read back and
    /// the URL re-signed (locally, no request) until it matches; a URL whose signed lifetime exceeds the requested
    /// one is never returned.
    /// </remarks>
    public async Task<Uri?> GetDownloadUrlAsync(BlobKey key, TimeSpan expiresIn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var lifetimeSeconds = Math.Max(1L, (long)ClampLifetime(expiresIn).TotalSeconds);
        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);

        var expires = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(lifetimeSeconds);
        for (var attempt = 1; ; attempt++)
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = key.Value,
                Verb = HttpVerb.GET,
                Protocol = _urlProtocol,
                Expires = expires,
            };

            var url = new Uri(await _client.GetPreSignedURLAsync(request).ConfigureAwait(false), UriKind.Absolute);
            var signedSeconds = SignedLifetimeSeconds(url);
            if (signedSeconds == lifetimeSeconds || (attempt == MaxSigningAttempts && signedSeconds is > 0 && signedSeconds < lifetimeSeconds))
            {
                return url;
            }

            if (attempt == MaxSigningAttempts || signedSeconds is null)
            {
                throw new InvalidOperationException("The S3 client did not produce a presigned URL within the requested lifetime.");
            }

            expires = expires.AddSeconds(lifetimeSeconds - signedSeconds.Value);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _bucketGate.Dispose();

    /// <summary>Validates and caps a download URL lifetime (positive, at most <see cref="MaxDownloadUrlLifetime"/>).</summary>
    internal static TimeSpan ClampLifetime(TimeSpan expiresIn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresIn, TimeSpan.Zero);
        return expiresIn > MaxDownloadUrlLifetime ? MaxDownloadUrlLifetime : expiresIn;
    }

    /// <summary>The signed lifetime (<c>X-Amz-Expires</c>, seconds) of a SigV4 presigned URL, or null when absent.</summary>
    internal static long? SignedLifetimeSeconds(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        foreach (var pair in url.Query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0
                && string.Equals(pair[..separator], ExpiresParameter, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(pair[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return seconds;
            }
        }

        return null;
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == NoSuchBucket)
        {
            throw BucketMissing(ex);
        }
    }

    /// <summary>Verifies (or, with CreateIfMissing, creates) the bucket once; a failure is retried on the next call.</summary>
    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (_bucketVerified)
        {
            return;
        }

        await _bucketGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bucketVerified)
            {
                return;
            }

            if (!await BucketExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_createIfMissing)
                {
                    throw BucketMissing(null);
                }

                await CreateBucketAsync(cancellationToken).ConfigureAwait(false);
            }

            _bucketVerified = true;
        }
        finally
        {
            _bucketGate.Release();
        }
    }

    private async Task<bool> BucketExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.HeadBucketAsync(new HeadBucketRequest { BucketName = _bucket }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            LogBucketNotVerifiable(_logger, _bucket);
            return true;
        }
    }

    private async Task CreateBucketAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket, UseClientRegion = true }, cancellationToken)
                .ConfigureAwait(false);
            LogBucketCreated(_logger, _bucket);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou")
        {
            // Created concurrently by another instance.
        }
    }

    private InvalidOperationException BucketMissing(Exception? inner)
    {
        _bucketVerified = false;
        return new InvalidOperationException(
            $"The S3 bucket '{_bucket}' configured in {S3BlobOptions.BucketKey} does not exist. Create it, or set " +
            $"{S3BlobOptions.CreateIfMissingKey}=true to create it at first use (development only).",
            inner);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created S3 bucket {Bucket} (Blob:S3:CreateIfMissing=true)")]
    private static partial void LogBucketCreated(ILogger logger, string bucket);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Access to S3 bucket {Bucket} could not be verified (HEAD returned 403); continuing, operations report their own errors")]
    private static partial void LogBucketNotVerifiable(ILogger logger, string bucket);
}
