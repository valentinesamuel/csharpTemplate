using System.Globalization;
using Acme.Service.BuildingBlocks.Web.Errors;
using Acme.Service.BuildingBlocks.Web.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Acme.Service.Modules.Auth.Sessions;

/// <summary>
/// Renders failures of the <c>live-session</c> policy (D-29): 503 <c>session_store_unavailable</c> with
/// <c>Retry-After</c> when the session lookup could not complete, 401 <c>session_revoked</c> when the session is not
/// live. Every other authorization outcome is handled by the default handler (challenge 401, forbid 403).
/// </summary>
internal sealed class LiveSessionResultHandler : IAuthorizationMiddlewareResultHandler
{
    public const string StoreUnavailableCode = "session_store_unavailable";

    public const string SessionRevokedCode = "session_revoked";

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        var reasons = authorizeResult.AuthorizationFailure?.FailureReasons.OfType<LiveSessionFailureReason>().ToList() ?? [];
        if (authorizeResult.Succeeded || reasons.Count == 0)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
            return;
        }

        int status;
        string code;
        string detail;
        if (reasons.Exists(r => r.StoreUnavailable))
        {
            status = StatusCodes.Status503ServiceUnavailable;
            code = StoreUnavailableCode;
            detail = "The session could not be verified. Try again later.";
            context.Response.Headers[HeaderNames.RetryAfter] =
                ResultHttpExtensions.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            status = StatusCodes.Status401Unauthorized;
            code = SessionRevokedCode;
            detail = "The session has ended. Sign in again.";
            context.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer error=\"invalid_token\"";
        }

        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Detail = detail };
        problem.Extensions[ProblemDetailsSetup.CodeKey] = code;
        var problemDetails = context.RequestServices.GetService<IProblemDetailsService>();
        if (problemDetails is null || !await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }).ConfigureAwait(false))
        {
            await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", context.RequestAborted)
                .ConfigureAwait(false);
        }
    }
}
