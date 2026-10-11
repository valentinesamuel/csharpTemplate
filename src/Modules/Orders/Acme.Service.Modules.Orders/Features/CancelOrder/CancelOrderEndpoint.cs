using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Orders.Contracts;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Orders.Features.CancelOrder;

/// <summary>
/// <c>POST /api/v1/orders/{id}/cancel</c> (the owner with <c>orders.cancel</c>, or <c>orders.manage</c>): Pending to
/// Cancelled.
/// </summary>
internal sealed class CancelOrderEndpoint : IEndpoint
{
    public const string Path = OrdersHttp.ItemPath + "/cancel";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (Guid id, HttpContext context, IUseCaseExecutor<CancelOrderCommand, OrderResponse> executor) =>
            {
                if (!OrdersCaller.TryFrom(context.User, out var caller))
                {
                    return OrdersHttp.MissingClaims();
                }

                return (await executor.ExecuteAsync(new CancelOrderCommand(id, caller), context.RequestAborted).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAnyPermission(OrdersPermissions.Cancel, OrdersPermissions.Manage)
            .WithOrdersMetadata("CancelOrder", "Cancels a pending order.")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
