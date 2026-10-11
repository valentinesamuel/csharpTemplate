using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.UnassignRole;

/// <summary>
/// <c>DELETE /api/v1/auth/users/{id}/roles/{role}</c> (<c>users.manage</c>, <c>live-session</c>): removes a role. 204;
/// 409 <c>last_admin</c> when it would leave no administrator.
/// </summary>
internal sealed class UnassignRoleEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/users/{id:guid}/roles/{role}";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete(Path, async (Guid id, string role, HttpContext context, IUseCaseExecutor<UnassignRoleCommand, Done> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var actorId))
                {
                    return AccountHttp.MissingClaims();
                }

                var result = await executor.ExecuteAsync(new UnassignRoleCommand(actorId, id, role), context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(_ => TypedResults.NoContent());
            })
            .RequireAdmin(AuthPermissions.UsersManage)
            .WithName("UnassignRole")
            .WithSummary("Removes a role from a user. Takes effect at the user's next token refresh; the last administrator keeps Admin.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
