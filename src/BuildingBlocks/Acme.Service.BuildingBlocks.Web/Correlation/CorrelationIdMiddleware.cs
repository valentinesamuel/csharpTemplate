using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Web.Correlation;

/// <summary>
/// Accepts a valid inbound <c>X-Correlation-Id</c> or generates one from the trace id (D-25).
/// An invalid inbound value is never echoed. The id is echoed in the response, stored in
/// <see cref="ICorrelationIdAccessor"/> and pushed onto the log scope.
/// </summary>
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

        accessor.CorrelationId = correlationId;
        context.Response.Headers[CorrelationId.HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationId.PropertyName] = correlationId }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rejected invalid inbound correlation id; generated {CorrelationId}")]
    private static partial void LogInvalidCorrelationId(ILogger logger, string correlationId);
}
