using System.Reflection;
using Acme.Service.BuildingBlocks.Web.Correlation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Acme.Service.BuildingBlocks.Web.Results;

/// <summary>The success body of every response that has one (ADR-0017). Failures are RFC 7807 problems carrying the same ids.</summary>
/// <param name="IsSuccess">Always <see langword="true"/>.</param>
/// <param name="RequestId">Alphanumeric per-request id, the same value the logs carry as <c>requestId</c>.</param>
/// <param name="CorrelationId">The correlation id of the operation.</param>
/// <param name="Data">The response DTO.</param>
public sealed record ApiEnvelope(bool IsSuccess, string RequestId, string CorrelationId, object? Data);

/// <summary>
/// Wraps a result that carries a value (200, 201, 202, ...) in an <see cref="ApiEnvelope"/>, keeping its status code and
/// <c>Location</c>. Results without a value (204, redirects, streams) pass through unchanged.
/// </summary>
public static class EnvelopeResult
{
    /// <summary>Wraps <paramref name="inner"/>.</summary>
    public static IResult Wrap(IResult inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return inner is IValueHttpResult { Value: not null } ? new Enveloped(inner) : inner;
    }

    private sealed class Enveloped(IResult inner) : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => (inner as IStatusCodeHttpResult)?.StatusCode;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var location = inner.GetType().GetProperty("Location", BindingFlags.Public | BindingFlags.Instance)?.GetValue(inner) as string;
            if (!string.IsNullOrEmpty(location))
            {
                httpContext.Response.Headers.Location = location;
            }

            var (requestId, correlationId) = RequestId.Of(httpContext);
            var envelope = new ApiEnvelope(true, requestId, correlationId, ((IValueHttpResult)inner).Value);
            return TypedResults.Json(envelope, statusCode: StatusCode ?? StatusCodes.Status200OK).ExecuteAsync(httpContext);
        }
    }
}
