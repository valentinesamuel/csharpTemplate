using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.LoginMfa;

/// <summary>
/// <c>POST /api/v1/auth/login/mfa</c> (anonymous, rate limit <c>auth-credentials</c>): completes a two-factor sign-in.
/// 200 tokens (<c>amr=[pwd,mfa]</c>), 401 <c>invalid_mfa_token</c> or <c>invalid_mfa_code</c>.
/// </summary>
internal sealed class LoginMfaEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/login/mfa";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (LoginMfaRequest request, HttpContext context, IUseCaseExecutor<LoginMfaCommand, TokenResponse> executor) =>
            {
                var command = new LoginMfaCommand(request.MfaToken, request.Code, request.RecoveryCode, AccountHttp.ClientOf(context));
                return (await executor.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false)).ToHttpResult();
            })
            .AllowAnonymous()
            .RequireCredentialsRateLimiting()
            .WithName("LoginMfa")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Completes a sign-in with two-factor authentication: the mfa token and an authenticator code or a recovery code.")
            .Produces<TokenResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }
}
