using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.ListRoles;

/// <summary><c>GET /api/v1/auth/roles</c> (<c>roles.read</c>, <c>live-session</c>): the roles with their permissions.</summary>
internal sealed class ListRolesEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/roles";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, async (HttpContext context, IUseCaseExecutor<ListRolesQuery, IReadOnlyList<RoleResponse>> executor) =>
                (await executor.ExecuteAsync(new ListRolesQuery(), context.RequestAborted).ConfigureAwait(false)).ToHttpResult())
            .RequireAdmin(AuthPermissions.RolesRead)
            .WithName("ListRoles")
            .WithSummary("Lists the roles and the permissions each one grants.")
            .Produces<IReadOnlyList<RoleResponse>>();
    }
}
