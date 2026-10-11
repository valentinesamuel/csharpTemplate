using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.UnlockUser;

/// <summary><c>POST /api/v1/auth/users/{id}/unlock</c> (<c>users.manage</c>, <c>live-session</c>): unlocks the account. 204.</summary>
internal sealed class UnlockUserEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/users/{id:guid}/unlock";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (Guid id, HttpContext context, IUseCaseExecutor<UnlockUserCommand, Done> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var actorId))
                {
                    return AccountHttp.MissingClaims();
                }

                var result = await executor.ExecuteAsync(new UnlockUserCommand(actorId, id), context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(_ => TypedResults.NoContent());
            })
            .RequireAdmin(AuthPermissions.UsersManage)
            .WithName("UnlockUser")
            .WithSummary("Unlocks a user account and resets its failed sign-in counter.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
