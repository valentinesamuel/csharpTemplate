using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Authorization;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Orders.Contracts;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Orders.Features.DeleteOrder;

/// <summary><c>DELETE /api/v1/orders/{id}</c> (<c>orders.manage</c>): soft delete. 204.</summary>
internal sealed class DeleteOrderEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete(OrdersHttp.ItemPath, async (Guid id, HttpContext context, IUseCaseExecutor<DeleteOrderCommand, Done> executor) =>
                (await executor.ExecuteAsync(new DeleteOrderCommand(id), context.RequestAborted).ConfigureAwait(false))
                    .ToHttpResult(_ => TypedResults.NoContent()))
            .RequirePermission(OrdersPermissions.Manage)
            .WithOrdersMetadata("DeleteOrder", "Deletes an order (soft delete).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
