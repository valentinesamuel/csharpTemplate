using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Auth.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Auth.Features.Me;

/// <summary><c>GET /api/v1/auth/me</c> (authenticated): the caller's account.</summary>
internal sealed class MeEndpoint : IEndpoint
{
    public const string Path = AccountHttp.Prefix + "/me";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, async (HttpContext context, IUseCaseExecutor<MeQuery, MeResponse> executor) =>
            {
                if (!AccountHttp.TryGetUserId(context.User, out var userId))
                {
                    return AccountHttp.MissingClaims();
                }

                return (await executor.ExecuteAsync(new MeQuery(userId), context.RequestAborted).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAuthorization()
            .WithName("GetMe")
            .WithTags(AccountHttp.Tag)
            .WithSummary("Returns the caller's account, roles and permissions.")
            .Produces<MeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
