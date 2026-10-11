using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.Refresh;

/// <summary><c>POST /api/v1/auth/refresh</c> (anonymous, rate limit <c>auth-credentials</c>): 200 tokens or 401.</summary>
internal sealed class RefreshEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/refresh";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (RefreshTokenRequest request, HttpContext context, IUseCaseExecutor<RefreshCommand, TokenResponse> executor) =>
            {
                var command = new RefreshCommand(request.RefreshToken, AccountHttp.ClientOf(context));
                return (await executor.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false)).ToHttpResult();
            })
            .AllowAnonymous()
            .RequireCredentialsRateLimiting()
            .WithName("RefreshTokens")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Exchanges a refresh token (single use) for a new access token and refresh token.")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }
}
