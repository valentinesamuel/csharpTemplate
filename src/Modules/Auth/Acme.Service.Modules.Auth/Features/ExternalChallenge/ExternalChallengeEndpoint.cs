using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.External;
using Acme.Service.Modules.Auth.Features.ExternalCallback;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Features.ExternalChallenge;

/// <summary>
/// <c>GET /api/v1/auth/external/oidc/challenge?returnUrl=</c> (anonymous): starts the external login by redirecting to
/// the provider. <c>returnUrl</c> must EXACTLY equal one of <c>Auth:External:Oidc:AllowedReturnUrls</c>, else 400
/// <c>invalid_return_url</c> (no open redirect). The URL travels in the handler's protected state, not in a cookie of
/// ours. 503 <c>external_provider_unavailable</c> when the provider's discovery document cannot be read. Mapped only
/// when the external login is enabled.
/// </summary>
internal sealed partial class ExternalChallengeEndpoint(IOptions<ExternalOidcOptions> options, ILogger<ExternalChallengeEndpoint> logger) : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/external/oidc/challenge";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        endpoints.MapGet(Path, async (string? returnUrl, HttpContext context, SignInManager<AuthUser> signInManager) =>
            {
                if (!options.Value.IsAllowedReturnUrl(returnUrl))
                {
                    return Result.Failure(ExternalErrors.InvalidReturnUrl).ToHttpResult();
                }

                var properties = signInManager.ConfigureExternalAuthenticationProperties(ExternalOidcOptions.Scheme, ExternalCallbackEndpoint.Path);
                properties.Items[ExternalAuthSetup.ReturnUrlItem] = returnUrl;

                try
                {
                    await context.ChallengeAsync(ExternalOidcOptions.Scheme, properties).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException
                    && !context.Response.HasStarted
                    && !context.RequestAborted.IsCancellationRequested)
                {
                    LogProviderUnavailable(logger, ex.GetType().Name);
                    return Result.Failure(ExternalErrors.ProviderUnavailable).ToHttpResult();
                }

                return Results.Empty;
            })
            .AllowAnonymous()
            .WithName("ExternalOidcChallenge")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Starts a sign-in with the external OpenID Connect provider; redirects to the provider.")
            .Produces(StatusCodes.Status302Found)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    // The exception message may contain the provider's URL but no secret; only the type is logged.
    [LoggerMessage(Level = LogLevel.Warning, Message = "External login challenge failed: the provider could not be reached ({ExceptionType})")]
    private static partial void LogProviderUnavailable(ILogger logger, string exceptionType);
}
