using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.External;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Features.ExternalCallback;

/// <summary>
/// <c>GET /api/v1/auth/external/oidc/callback</c> (anonymous): where the OIDC handler sends the browser after the
/// provider's response was validated and the provider's identity was put in the external cookie. Reads that identity
/// (<see cref="SignInManager{TUser}.GetExternalLoginInfoAsync"/>), runs <see cref="ExternalCallbackUseCase"/>, deletes the
/// external cookie and redirects to the return URL chosen at the challenge (checked again against the allowlist) with
/// <c>?code=</c> (a one-time code, 60 seconds), <c>?error=account_exists</c> or <c>?error=external_login_failed</c>. No
/// token is ever put in a URL. 400 when there is no external identity or no allowed return URL. Mapped only when the
/// external login is enabled.
/// </summary>
internal sealed class ExternalCallbackEndpoint(IOptions<ExternalOidcOptions> options) : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/external/oidc/callback";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        endpoints.MapGet(Path, async (
                HttpContext context,
                SignInManager<AuthUser> signInManager,
                IUseCaseExecutor<ExternalCallbackCommand, ExternalCallbackResult> executor) =>
            {
                var login = await signInManager.GetExternalLoginInfoAsync().ConfigureAwait(false);
                if (login is null)
                {
                    return Result.Failure(ExternalErrors.LoginFailed).ToHttpResult();
                }

                // The external identity is single-use: whatever happens next, the cookie goes.
                await context.SignOutAsync(IdentityConstants.ExternalScheme).ConfigureAwait(false);

                var returnUrl = login.AuthenticationProperties?.Items.TryGetValue(ExternalAuthSetup.ReturnUrlItem, out var value) == true ? value : null;
                if (!options.Value.IsAllowedReturnUrl(returnUrl))
                {
                    return Result.Failure(ExternalErrors.InvalidReturnUrl).ToHttpResult();
                }

                var result = await executor.ExecuteAsync(new ExternalCallbackCommand(login), context.RequestAborted).ConfigureAwait(false);
                var target = result switch
                {
                    { IsFailure: true } => ExternalAuthSetup.AppendToReturnUrl(returnUrl!, "error", ExternalAuthSetup.LoginFailedError),
                    { Value.Code: { } code } => ExternalAuthSetup.AppendToReturnUrl(returnUrl!, "code", code),
                    _ => ExternalAuthSetup.AppendToReturnUrl(returnUrl!, "error", ExternalAuthSetup.AccountExistsError),
                };

                return TypedResults.Redirect(target);
            })
            .AllowAnonymous()
            .WithName("ExternalOidcCallback")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Completes the external sign-in and redirects to the client with a one-time code or an error.")
            .Produces(StatusCodes.Status302Found)
            .ProducesValidationProblem();
    }
}
