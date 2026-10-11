using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.AssignRole;

/// <summary>
/// <c>POST /api/v1/auth/users/{id}/roles/{role}</c> (<c>users.manage</c>, <c>live-session</c>): grants a role. 204; the
/// change reaches the user's tokens at the next refresh.
/// </summary>
internal sealed class AssignRoleEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/users/{id:guid}/roles/{role}";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (Guid id, string role, HttpContext context, IUseCaseExecutor<AssignRoleCommand, Done> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var actorId))
                {
                    return AccountHttp.MissingClaims();
                }

                var result = await executor.ExecuteAsync(new AssignRoleCommand(actorId, id, role), context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(_ => TypedResults.NoContent());
            })
            .RequireAdmin(AuthPermissions.UsersManage)
            .WithName("AssignRole")
            .WithSummary("Grants a role to a user. Takes effect at the user's next token refresh.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
