using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.External;
using Acme.Service.Modules.Auth.Features.Login;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Features.ExternalExchange;

/// <summary>
/// <c>POST /api/v1/auth/external/exchange</c> (anonymous, rate limit <c>auth-credentials</c>): exchanges the one-time code
/// of an external login, once, for tokens (<c>amr=[ext]</c>) or <c>{mfaRequired:true, mfaToken}</c>. 401
/// <c>invalid_exchange_code</c>. Mapped only when the external login is enabled.
/// </summary>
internal sealed class ExternalExchangeEndpoint(IOptions<ExternalOidcOptions> options) : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/external/exchange";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        endpoints.MapPost(Path, async (ExternalExchangeRequest request, HttpContext context, IUseCaseExecutor<ExternalExchangeCommand, LoginResponse> executor) =>
            {
                var command = new ExternalExchangeCommand(request.Code, AccountHttp.ClientOf(context));
                var result = await executor.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(response => response.Mfa is { } mfa ? TypedResults.Ok(mfa) : (IResult)TypedResults.Ok(response.Tokens));
            })
            .AllowAnonymous()
            .RequireCredentialsRateLimiting()
            .WithName("ExternalExchange")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Exchanges the one-time code of an external sign-in for an access token and a refresh token, or an mfa token when two-factor authentication is enabled.")
            .Produces<TokenResponse>()
            .Produces<MfaRequiredResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }
}
