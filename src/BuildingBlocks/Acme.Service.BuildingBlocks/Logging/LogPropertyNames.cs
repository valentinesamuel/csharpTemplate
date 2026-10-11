using System.Collections.Frozen;

namespace Acme.Service.BuildingBlocks.Logging;

/// <summary>
/// Single source of truth for log property names that carry context rather than call-site data. The JSON log
/// formatter never writes reserved names into <c>meta</c>. Names come in two tiers: <see cref="AlwaysReserved"/>
/// (never in <c>meta</c>) and <see cref="SummaryOnlyReserved"/> (excluded only on the request-summary event, i.e.
/// an event carrying <see cref="HttpStatusCode"/>; on any other event they are ordinary call-site properties).
/// </summary>
/// <remarks>
/// The dotted <c>Http.*</c> names cannot be produced by message template holes (a hole cannot contain <c>.</c>), so no
/// call site can collide with them by logging; a collision is possible only through an explicit
/// <c>BeginScope</c> or <c>ForContext</c>, which is a deliberate act.
/// </remarks>
public static class LogPropertyNames
{
    /// <summary>The correlation id of the current logical operation (HTTP request, job run, outbox dispatch, CLI verb).</summary>
    public const string CorrelationId = "CorrelationId";

    /// <summary>The ASP.NET Core request id (<c>HttpContext.TraceIdentifier</c>).</summary>
    public const string RequestId = "RequestId";

    /// <summary>The logger category.</summary>
    public const string SourceContext = "SourceContext";

    /// <summary>The Microsoft.Extensions.Logging event id.</summary>
    public const string EventId = "EventId";

    /// <summary>The W3C trace id.</summary>
    public const string TraceId = "TraceId";

    /// <summary>The W3C span id.</summary>
    public const string SpanId = "SpanId";

    /// <summary>The parent span id.</summary>
    public const string ParentId = "ParentId";

    /// <summary>The request path added by the ASP.NET Core hosting scope and the request-summary template.</summary>
    public const string RequestPath = "RequestPath";

    /// <summary>The connection id added by the ASP.NET Core hosting scope.</summary>
    public const string ConnectionId = "ConnectionId";

    /// <summary>The MVC action id.</summary>
    public const string ActionId = "ActionId";

    /// <summary>The MVC action name.</summary>
    public const string ActionName = "ActionName";

    /// <summary>The scope list property.</summary>
    public const string Scope = "Scope";

    /// <summary>The original message template recorded by Microsoft.Extensions.Logging.</summary>
    public const string OriginalFormat = "{OriginalFormat}";

    /// <summary>HTTP request method (dotted; cannot be produced by a message template hole).</summary>
    public const string HttpMethod = "Http.Method";

    /// <summary>HTTP request path, <c>PathBase + Path</c> (dotted; cannot be produced by a message template hole).</summary>
    public const string HttpPath = "Http.Path";

    /// <summary>HTTP response status code (dotted; cannot be produced by a message template hole).</summary>
    public const string HttpStatusCode = "Http.StatusCode";

    /// <summary>HTTP request duration in milliseconds (dotted; cannot be produced by a message template hole).</summary>
    public const string HttpDurationMs = "Http.DurationMs";

    /// <summary>Whether the HTTP request succeeded (dotted; cannot be produced by a message template hole).</summary>
    public const string HttpIsSuccess = "Http.IsSuccess";

    /// <summary>Request-summary template property: the request method. Reserved only on the summary event.</summary>
    public const string RequestMethod = "RequestMethod";

    /// <summary>Request-summary template property: the status code. Reserved only on the summary event.</summary>
    public const string StatusCode = "StatusCode";

    /// <summary>Request-summary template property: the elapsed milliseconds. Reserved only on the summary event.</summary>
    public const string Elapsed = "Elapsed";

    /// <summary>Names that never appear in <c>meta</c> (ordinal comparison).</summary>
    public static IReadOnlySet<string> AlwaysReserved { get; } = new[]
    {
        CorrelationId,
        RequestId,
        SourceContext,
        EventId,
        TraceId,
        SpanId,
        ParentId,
        RequestPath,
        ConnectionId,
        ActionId,
        ActionName,
        Scope,
        OriginalFormat,
        HttpMethod,
        HttpPath,
        HttpStatusCode,
        HttpDurationMs,
        HttpIsSuccess,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Names excluded from <c>meta</c> only on the request-summary event (the event carrying
    /// <see cref="HttpStatusCode"/>); ordinal comparison.
    /// </summary>
    public static IReadOnlySet<string> SummaryOnlyReserved { get; } = new[]
    {
        RequestMethod,
        StatusCode,
        Elapsed,
    }.ToFrozenSet(StringComparer.Ordinal);
}
