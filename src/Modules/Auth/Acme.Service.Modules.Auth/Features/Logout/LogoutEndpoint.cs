using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Refresh;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.Logout;

/// <summary>
/// <c>POST /api/v1/auth/logout</c> (authenticated): revokes the session of the given refresh token when it belongs to
/// the caller. Always 204. Outstanding access tokens stay valid until they expire (D-29).
/// </summary>
internal sealed class LogoutEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/logout";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (RefreshTokenRequest request, HttpContext context, IUseCaseExecutor<LogoutCommand, Done> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var userId))
                {
                    return AccountHttp.MissingClaims();
                }

                var result = await executor.ExecuteAsync(new LogoutCommand(userId, request.RefreshToken), context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(_ => TypedResults.NoContent());
            })
            .RequireAuthorization()
            .WithName("Logout")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Ends the session of a refresh token.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }
}
