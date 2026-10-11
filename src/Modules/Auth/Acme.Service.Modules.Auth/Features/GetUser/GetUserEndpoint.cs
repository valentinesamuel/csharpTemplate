using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.GetUser;

/// <summary><c>GET /api/v1/auth/users/{id}</c> (<c>users.read</c>, <c>live-session</c>): one account, 404 when unknown.</summary>
internal sealed class GetUserEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/users/{id:guid}";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, async (Guid id, HttpContext context, IUseCaseExecutor<GetUserQuery, UserResponse> executor) =>
                (await executor.ExecuteAsync(new GetUserQuery(id), context.RequestAborted).ConfigureAwait(false)).ToHttpResult())
            .RequireAdmin(AuthPermissions.UsersRead)
            .WithName("GetUser")
            .WithSummary("Returns a user account with its roles, lock state and linked external providers.")
            .Produces<UserResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
