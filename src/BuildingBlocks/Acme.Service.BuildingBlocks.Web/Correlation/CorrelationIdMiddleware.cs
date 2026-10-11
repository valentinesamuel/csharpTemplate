using Acme.Service.BuildingBlocks.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Web.Correlation;

/// <summary>
/// Accepts a valid inbound <c>X-Correlation-Id</c> or generates one from the trace id (D-25).
/// An invalid inbound value is never echoed. The id is echoed in the response, stored in
/// <see cref="ICorrelationIdAccessor"/> and pushed onto the log scope.
/// </summary>
/// <remarks>
/// Second responsibility (D-02): the same single log scope carries the request context for every line logged inside
/// the request: <c>CorrelationId</c>, <c>RequestId</c> (<see cref="HttpContext.TraceIdentifier"/>), <c>Http.Method</c>
/// and <c>Http.Path</c> (<see cref="RequestLogValues.Path"/>: <c>PathBase + Path</c>). The query string and headers
/// other than <c>X-Correlation-Id</c> are never read (INV-28). Only Microsoft.Extensions.Logging is used here.
/// </remarks>
public sealed partial class CorrelationIdMiddleware(
    RequestDelegate next,
    ICorrelationIdAccessor accessor,
    ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>Processes the request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var inbound = context.Request.Headers[CorrelationId.HeaderName];
        string correlationId;
        if (inbound.Count == 1 && CorrelationId.IsValid(inbound[0]))
        {
            correlationId = inbound[0]!;
        }
        else
        {
            correlationId = CorrelationId.Generate();
            if (inbound.Count > 0)
            {
                LogInvalidCorrelationId(logger, correlationId);
            }
        }

        // Kestrel ids contain ':'; the request id is alphanumeric everywhere it is shown (ADR-0017).
        context.TraceIdentifier = RequestId.Sanitize(context.TraceIdentifier);

        accessor.CorrelationId = correlationId;
        context.Response.Headers[CorrelationId.HeaderName] = correlationId;
        context.Response.Headers[RequestId.HeaderName] = context.TraceIdentifier;

        var scope = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [CorrelationId.PropertyName] = correlationId,
            [LogPropertyNames.RequestId] = context.TraceIdentifier,
            [LogPropertyNames.HttpMethod] = context.Request.Method,
            [LogPropertyNames.HttpPath] = RequestLogValues.Path(context.Request),
        };

        using (logger.BeginScope(scope))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rejected invalid inbound correlation id; generated {CorrelationId}")]
    private static partial void LogInvalidCorrelationId(ILogger logger, string correlationId);
}
