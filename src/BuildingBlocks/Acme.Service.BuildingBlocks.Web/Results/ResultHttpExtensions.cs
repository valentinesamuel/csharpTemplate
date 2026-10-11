using System.Globalization;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Web.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Acme.Service.BuildingBlocks.Web.Results;

/// <summary>
/// Converts use-case results to HTTP results (plan §0.6, INV-18, ADR-0017). Successes with a body are wrapped in an
/// <see cref="ApiEnvelope"/>. Failures become RFC 7807 <c>application/problem+json</c> with the error code in <c>code</c>;
/// <c>isSuccess</c>, <c>requestId</c>, <c>traceId</c> and <c>correlationId</c> are added by the ProblemDetails setup
/// (<see cref="ProblemDetailsSetup"/>).
/// </summary>
public static class ResultHttpExtensions
{
    /// <summary>Seconds sent in <c>Retry-After</c> with every 503 produced from an <see cref="ErrorType.Unavailable"/> error.</summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>Success: 204 No Content. Failure: the mapped problem response.</summary>
    public static IResult ToHttpResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblemResult();
    }

    /// <summary>Success: 200 OK with the value in an <see cref="ApiEnvelope"/>. Failure: the mapped problem response.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? EnvelopeResult.Wrap(TypedResults.Ok(result.Value)) : result.Error.ToProblemResult();
    }

    /// <summary>Success: the result of <paramref name="onSuccess"/> (for example 201 Created). Failure: the mapped problem response.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? EnvelopeResult.Wrap(onSuccess(result.Value)) : result.Error.ToProblemResult();
    }

    /// <summary>HTTP status for an <see cref="ErrorType"/>.</summary>
    public static int ToStatusCode(this ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown error type."),
    };

    internal static IResult ToProblemResult(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var status = error.Type.ToStatusCode();
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { [ProblemDetailsSetup.CodeKey] = error.Code };

        if (error.Type == ErrorType.Validation)
        {
            var errors = error.Details ?? new Dictionary<string, string[]>(StringComparer.Ordinal);
            return TypedResults.ValidationProblem(errors, detail: error.Message, extensions: extensions);
        }

        IResult problem = TypedResults.Problem(detail: error.Message, statusCode: status, extensions: extensions);
        return error.Type == ErrorType.Unavailable ? new RetryAfterResult(problem, RetryAfterSeconds) : problem;
    }

    internal static IResult Problem(int status, string code, string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: status,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { [ProblemDetailsSetup.CodeKey] = code });

    /// <summary>Adds <c>Retry-After</c> before executing the inner result.</summary>
    private sealed class RetryAfterResult(IResult inner, int seconds) : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => (inner as IStatusCodeHttpResult)?.StatusCode;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers[HeaderNames.RetryAfter] = seconds.ToString(CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
