using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.LockUser;

/// <summary>
/// <c>POST /api/v1/auth/users/{id}/lock</c> (<c>users.manage</c>, <c>live-session</c>): locks the account and revokes all
/// of its sessions. 204.
/// </summary>
internal sealed class LockUserEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/users/{id:guid}/lock";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (Guid id, HttpContext context, IUseCaseExecutor<LockUserCommand, Done> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var actorId))
                {
                    return AccountHttp.MissingClaims();
                }

                var result = await executor.ExecuteAsync(new LockUserCommand(actorId, id), context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(_ => TypedResults.NoContent());
            })
            .RequireAdmin(AuthPermissions.UsersManage)
            .WithName("LockUser")
            .WithSummary("Locks a user account until it is unlocked and ends all of its sessions.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
