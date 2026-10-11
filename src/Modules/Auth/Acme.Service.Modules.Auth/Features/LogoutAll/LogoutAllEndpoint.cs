using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.LogoutAll;

/// <summary>
/// <c>POST /api/v1/auth/logout-all</c> (authenticated and <c>live-session</c>, D-29): ends every session of the caller.
/// 204; once done, the same access token is rejected here with 401 <c>session_revoked</c>.
/// </summary>
internal sealed class LogoutAllEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/logout-all";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (HttpContext context, IUseCaseExecutor<LogoutAllCommand, Done> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var userId))
                {
                    return AccountHttp.MissingClaims();
                }

                var result = await executor.ExecuteAsync(new LogoutAllCommand(userId), context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(_ => TypedResults.NoContent());
            })
            .RequireAuthorization(LiveSessionRequirement.PolicyName)
            .WithName("LogoutAll")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Ends every session of the caller.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}
