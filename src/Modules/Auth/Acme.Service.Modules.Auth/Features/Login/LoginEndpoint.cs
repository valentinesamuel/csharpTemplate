using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.Login;

/// <summary>
/// <c>POST /api/v1/auth/login</c> (anonymous, rate limit <c>auth-credentials</c>): 200 tokens, 200
/// <c>{mfaRequired:true, mfaToken}</c> for an account with two-factor authentication, 401 <c>invalid_credentials</c> or
/// 403 <c>email_not_verified</c>.
/// </summary>
internal sealed class LoginEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/login";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (LoginRequest request, HttpContext context, IUseCaseExecutor<LoginCommand, LoginResponse> executor) =>
            {
                var command = new LoginCommand(request.Email, request.Password, AccountHttp.ClientOf(context));
                var result = await executor.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(response => response.Mfa is { } mfa ? TypedResults.Ok(mfa) : (IResult)TypedResults.Ok(response.Tokens));
            })
            .AllowAnonymous()
            .RequireCredentialsRateLimiting()
            .WithName("Login")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Signs in with email and password and returns an access token and a refresh token, or an mfa token when two-factor authentication is enabled.")
            .Produces<TokenResponse>()
            .Produces<MfaRequiredResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }
}
