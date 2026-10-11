using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Orders.Contracts;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Orders.Features.UpdateOrderLines;

/// <summary>
/// <c>PUT /api/v1/orders/{id}</c> (the owner with <c>orders.create</c>, or <c>orders.manage</c>): replaces the lines of a
/// pending order. The body carries the <c>version</c> last read; a stale one is 409.
/// </summary>
internal sealed class UpdateOrderLinesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut(OrdersHttp.ItemPath, async (Guid id, UpdateOrderLinesRequest body, HttpContext context, IUseCaseExecutor<UpdateOrderLinesCommand, OrderResponse> executor) =>
            {
                if (!OrdersCaller.TryFrom(context.User, out var caller))
                {
                    return OrdersHttp.MissingClaims();
                }

                var command = new UpdateOrderLinesCommand(id, caller, body.Version, body.Lines);
                return (await executor.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAnyPermission(OrdersPermissions.Create, OrdersPermissions.Manage)
            .WithOrdersMetadata("UpdateOrderLines", "Replaces the lines of a pending order.")
            .Produces<OrderResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
