using System.Diagnostics;
using Acme.Service.BuildingBlocks.Web.Correlation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Service.BuildingBlocks.Web.Errors;

/// <summary>
/// Uniform RFC 7807 error model (INV-18): every problem response carries <c>code</c>, <c>isSuccess</c>,
/// <c>requestId</c>, <c>traceId</c> and <c>correlationId</c>; exception details never leave the process outside Development.
/// </summary>
public static class ProblemDetailsSetup
{
    /// <summary>ProblemDetails extension holding the machine-readable error code.</summary>
    public const string CodeKey = "code";

    /// <summary>ProblemDetails extension holding the W3C trace id.</summary>
    public const string TraceIdKey = "traceId";

    /// <summary>ProblemDetails extension holding the correlation id.</summary>
    public const string CorrelationIdKey = "correlationId";

    /// <summary>ProblemDetails extension that is always <see langword="false"/>, mirroring the success envelope.</summary>
    public const string IsSuccessKey = "isSuccess";

    /// <summary>ProblemDetails extension holding the alphanumeric request id.</summary>
    public const string RequestIdKey = RequestId.PropertyName;

    /// <summary>Registers <see cref="IProblemDetailsService"/> with the uniform extensions.</summary>
    public static IServiceCollection AddProblemDetailsSetup(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = Customize);
        return services;
    }

    /// <summary>Adds the exception handler and status-code pages, both rendering ProblemDetails.</summary>
    public static IApplicationBuilder UseProblemDetailsSetup(this IApplicationBuilder app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }

    private static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var http = context.HttpContext;
        var status = problem.Status ?? http.Response.StatusCode;

        if (!problem.Extensions.ContainsKey(CodeKey))
        {
            problem.Extensions[CodeKey] = CodeFor(status);
        }

        var (requestId, correlationId) = RequestId.Of(http);
        problem.Extensions[IsSuccessKey] = false;
        problem.Extensions[RequestIdKey] = requestId;
        problem.Extensions[TraceIdKey] = Activity.Current?.TraceId.ToHexString() ?? requestId;
        problem.Extensions[CorrelationIdKey] = correlationId;

        var environment = http.RequestServices.GetService<IHostEnvironment>();
        if (context.Exception is not null && environment?.IsDevelopment() != true)
        {
            // Never leak exception messages or stack traces outside Development.
            problem.Detail = null;
            problem.Extensions.Remove("exception");
        }
    }

    private static string CodeFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "bad_request",
        StatusCodes.Status401Unauthorized => "unauthorized",
        StatusCodes.Status403Forbidden => "forbidden",
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status406NotAcceptable => "not_acceptable",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status422UnprocessableEntity => "unprocessable",
        StatusCodes.Status429TooManyRequests => "too_many_requests",
        StatusCodes.Status500InternalServerError => "internal_error",
        StatusCodes.Status503ServiceUnavailable => "unavailable",
        _ => status >= 500 ? "server_error" : "client_error",
    };
}
