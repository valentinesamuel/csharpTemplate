using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.BuildingBlocks.Web.Correlation;

/// <summary>
/// Request id rules (ADR-0017): the per-request id is <see cref="HttpContext.TraceIdentifier"/> reduced to
/// <c>[A-Za-z0-9]</c> (Kestrel ids look like <c>0HNP7R1GJ7MSQ:00000002</c>). Logs, the response header and the body
/// all carry this same value.
/// </summary>
public static class RequestId
{
    /// <summary>The HTTP response header carrying the request id.</summary>
    public const string HeaderName = "X-Request-Id";

    /// <summary>The ProblemDetails / envelope property name.</summary>
    public const string PropertyName = "requestId";

    /// <summary>Removes every character outside <c>[A-Za-z0-9]</c>; an empty result becomes a generated id.</summary>
    public static string Sanitize(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            var clean = string.Create(
                CountAlphanumeric(value),
                value,
                static (span, source) =>
                {
                    var i = 0;
                    foreach (var c in source)
                    {
                        if (char.IsAsciiLetterOrDigit(c))
                        {
                            span[i++] = c;
                        }
                    }
                });

            if (clean.Length > 0)
            {
                return clean;
            }
        }

        return CorrelationId.Generate();
    }

    /// <summary>The request id and correlation id of <paramref name="http"/> for response bodies.</summary>
    public static (string RequestId, string CorrelationId) Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var correlationId =
            http.RequestServices.GetService<ICorrelationIdAccessor>()?.CorrelationId
            ?? http.Response.Headers[CorrelationId.HeaderName].ToString();
        return (Sanitize(http.TraceIdentifier), correlationId);
    }

    private static int CountAlphanumeric(string value)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                count++;
            }
        }

        return count;
    }
}
