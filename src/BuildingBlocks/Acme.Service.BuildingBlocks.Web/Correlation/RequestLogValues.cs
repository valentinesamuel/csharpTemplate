using Microsoft.AspNetCore.Http;

namespace Acme.Service.BuildingBlocks.Web.Correlation;

/// <summary>
/// The HTTP values written to logs, computed in one place so that <see cref="CorrelationIdMiddleware"/> (every line
/// inside a request) and the request-summary event agree on them.
/// </summary>
public static class RequestLogValues
{
    /// <summary>
    /// The logged request path: <c>PathBase + Path</c> (decoded). Never the query string and never a header (INV-28).
    /// </summary>
    public static string Path(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (request.PathBase + request.Path).Value ?? string.Empty;
    }

    /// <summary>
    /// The request outcome: <see langword="true"/> when <paramref name="statusCode"/> is below 400 and no exception
    /// was observed (thrown out of the pipeline or handled by the exception handler).
    /// </summary>
    public static bool IsSuccess(int statusCode, bool exceptionObserved) =>
        statusCode < StatusCodes.Status400BadRequest && !exceptionObserved;
}
