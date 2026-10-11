using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using Acme.Service.BuildingBlocks.Caching;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Web.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Acme.Service.BuildingBlocks.Web.Idempotency;

/// <summary>
/// Idempotency-Key endpoint filter (plan P4). Scope key <c>idem:{sub}:{METHOD}:{routePattern}:{key}</c>;
/// request identity is the SHA-256 of the body. An atomic <see cref="ICache.TryAddAsync{T}"/> marker
/// guarantees that two concurrent first requests never both execute. Completed 2xx/4xx (except 409/429)
/// responses are replayed (status, body, Content-Type, Location only; never Set-Cookie).
/// </summary>
internal sealed partial class IdempotencyFilter(
    ICache cache,
    IOptions<IdempotencyOptions> options,
    ILogger<IdempotencyFilter> logger) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";
    public const int MaxKeyLength = 128;
    public const string InvalidKeyCode = "invalid_idempotency_key";
    public const string InProgressCode = "idempotency_in_progress";
    public const string KeyReusedCode = "idempotency_key_reused";
    public const string UnavailableCode = "idempotency_unavailable";
    internal const string AnonymousSubject = "anonymous";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var headerValues = http.Request.Headers[HeaderName];
        if (headerValues.Count == 0)
        {
            return await next(context).ConfigureAwait(false);
        }

        if (headerValues.Count != 1 || !IsValidKey(headerValues[0]))
        {
            return Error.Validation(
                InvalidKeyCode,
                "The Idempotency-Key header must be 1 to 128 characters of [A-Za-z0-9-_].",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [HeaderName] = ["Must be 1 to 128 characters of [A-Za-z0-9-_]."],
                }).ToProblemResult();
        }

        var settings = options.Value;
        var routePattern = GetRoutePattern(http);
        var scopeKey = BuildScopeKey(http, routePattern, headerValues[0]!);
        var aborted = http.RequestAborted;

        if (!cache.IsAvailable)
        {
            return await UnavailableAsync(context, next, settings, routePattern, null).ConfigureAwait(false);
        }

        var requestHash = await ComputeBodyHashAsync(http.Request, aborted).ConfigureAwait(false);

        bool added;
        IdempotencyRecord? existing = null;
        try
        {
            added = await cache.TryAddAsync(scopeKey, IdempotencyRecord.InProgress(requestHash), settings.InProgressTtl, aborted)
                .ConfigureAwait(false);
            if (!added)
            {
                existing = await cache.GetAsync<IdempotencyRecord>(scopeKey, aborted).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && aborted.IsCancellationRequested))
        {
            return await UnavailableAsync(context, next, settings, routePattern, ex).ConfigureAwait(false);
        }

        if (added)
        {
            return await ExecuteAndStoreAsync(context, next, settings, scopeKey, requestHash, routePattern).ConfigureAwait(false);
        }

        // The marker was not added: another request owns the key, or a completed response exists.
        if (existing is null || existing.State != IdempotencyRecord.CompletedState)
        {
            return Error.Conflict(InProgressCode, "A request with this Idempotency-Key is still being processed. Retry later.")
                .ToProblemResult();
        }

        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return Error.Unprocessable(KeyReusedCode, "This Idempotency-Key was already used with a different request body.")
                .ToProblemResult();
        }

        return new ReplayResult(existing);
    }

    internal static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    internal static bool ShouldStore(int statusCode) =>
        statusCode is >= 200 and < 300
        || (statusCode is >= 400 and < 500
            && statusCode != StatusCodes.Status409Conflict
            && statusCode != StatusCodes.Status429TooManyRequests);

    private static string GetRoutePattern(HttpContext http) =>
        (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value ?? string.Empty;

    private static string BuildScopeKey(HttpContext http, string routePattern, string key)
    {
        var subject = http.User.FindFirst("sub")?.Value
            ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? AnonymousSubject;
        return $"idem:{subject}:{http.Request.Method.ToUpperInvariant()}:{routePattern}:{key}";
    }

    private static async Task<string> ComputeBodyHashAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.Body.CanSeek)
        {
            request.EnableBuffering();
        }

        var body = request.Body;
        body.Position = 0;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        body.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async ValueTask<object?> UnavailableAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        IdempotencyOptions settings,
        string routePattern,
        Exception? exception)
    {
        var method = context.HttpContext.Request.Method;
        if (settings.FailMode == IdempotencyFailMode.Open)
        {
            LogProceedingWithoutIdempotency(logger, exception, method, routePattern);
            return await next(context).ConfigureAwait(false);
        }

        LogRejectedUnavailable(logger, exception, method, routePattern);
        return Error.Unavailable(UnavailableCode, "Idempotency is temporarily unavailable. Retry later.").ToProblemResult();
    }

    private async ValueTask<object?> ExecuteAndStoreAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        IdempotencyOptions settings,
        string scopeKey,
        string requestHash,
        string routePattern)
    {
        var http = context.HttpContext;
        var response = http.Response;
        var originalBody = response.Body;

        using var captured = new MemoryStream();
        try
        {
            var result = await next(context).ConfigureAwait(false);

            // Execute the result into a buffer so the exact status and body can be stored for replay.
            response.Body = captured;
            await WriteResultAsync(http, result).ConfigureAwait(false);
        }
        catch
        {
            response.Body = originalBody;
            await RemoveMarkerAsync(scopeKey, http.Request.Method, routePattern).ConfigureAwait(false);
            throw;
        }

        response.Body = originalBody;
        var statusCode = response.StatusCode;
        var body = captured.ToArray();

        if (ShouldStore(statusCode))
        {
            var completed = IdempotencyRecord.Completed(
                requestHash,
                statusCode,
                response.ContentType,
                response.Headers.Location.Count > 0 ? response.Headers.Location.ToString() : null,
                body);
            try
            {
                await cache.SetAsync(scopeKey, completed, settings.Ttl, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogStoreFailed(logger, ex, http.Request.Method, routePattern);
            }
        }
        else
        {
            await RemoveMarkerAsync(scopeKey, http.Request.Method, routePattern).ConfigureAwait(false);
        }

        if (body.Length > 0)
        {
            await originalBody.WriteAsync(body, http.RequestAborted).ConfigureAwait(false);
        }

        return TypedResults.Empty;
    }

    private static async Task WriteResultAsync(HttpContext http, object? result)
    {
        switch (result)
        {
            case null:
                break;
            case IResult httpResult:
                await httpResult.ExecuteAsync(http).ConfigureAwait(false);
                break;
            case string text:
                await TypedResults.Text(text).ExecuteAsync(http).ConfigureAwait(false);
                break;
            default:
                await TypedResults.Json(result).ExecuteAsync(http).ConfigureAwait(false);
                break;
        }
    }

    private async Task RemoveMarkerAsync(string scopeKey, string method, string routePattern)
    {
        try
        {
            await cache.RemoveAsync(scopeKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRemoveFailed(logger, ex, method, routePattern);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Idempotency cache unavailable; processing {Method} {RoutePattern} without idempotency (FailMode Open)")]
    private static partial void LogProceedingWithoutIdempotency(ILogger logger, Exception? exception, string method, string routePattern);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Idempotency cache unavailable; rejected {Method} {RoutePattern} (FailMode Closed)")]
    private static partial void LogRejectedUnavailable(ILogger logger, Exception? exception, string method, string routePattern);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to store the idempotent response for {Method} {RoutePattern}")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception, string method, string routePattern);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to remove the idempotency marker for {Method} {RoutePattern}")]
    private static partial void LogRemoveFailed(ILogger logger, Exception exception, string method, string routePattern);

    /// <summary>Replays a stored response. Only status, Content-Type, Location and body are restored.</summary>
    private sealed class ReplayResult(IdempotencyRecord record) : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => record.StatusCode;

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.OnStarting(static state =>
            {
                // Cookies belong to the original exchange; never replay or add them (P4 failure mode).
                ((HttpResponse)state).Headers.Remove(HeaderNames.SetCookie);
                return Task.CompletedTask;
            }, response);

            response.Headers.Remove(HeaderNames.SetCookie);
            response.StatusCode = record.StatusCode;
            response.Headers[ReplayedHeaderName] = "true";
            if (record.ContentType is not null)
            {
                response.ContentType = record.ContentType;
            }

            if (record.Location is not null)
            {
                response.Headers.Location = record.Location;
            }

            if (record.Body is { Length: > 0 } body)
            {
                await response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Cache entry for one idempotency scope key. Serialized by the cache provider (System.Text.Json).</summary>
internal sealed class IdempotencyRecord
{
    public const string InProgressState = "in_progress";
    public const string CompletedState = "completed";

    public string State { get; set; } = InProgressState;

    public string RequestHash { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public string? ContentType { get; set; }

    public string? Location { get; set; }

    public byte[]? Body { get; set; }

    public static IdempotencyRecord InProgress(string requestHash) =>
        new() { State = InProgressState, RequestHash = requestHash };

    public static IdempotencyRecord Completed(string requestHash, int statusCode, string? contentType, string? location, byte[] body) =>
        new()
        {
            State = CompletedState,
            RequestHash = requestHash,
            StatusCode = statusCode,
            ContentType = contentType,
            Location = location,
            Body = body,
        };
}
