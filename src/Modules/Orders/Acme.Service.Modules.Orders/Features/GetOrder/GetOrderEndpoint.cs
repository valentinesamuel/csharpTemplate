using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Authorization;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Orders.Contracts;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Orders.Features.GetOrder;

/// <summary>
/// <c>GET /api/v1/orders/{id}</c> (<c>orders.read</c>): the caller's own order, or any order with <c>orders.manage</c>;
/// otherwise 404.
/// </summary>
internal sealed class GetOrderEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(OrdersHttp.ItemPath, async (Guid id, HttpContext context, IUseCaseExecutor<GetOrderQuery, OrderResponse> executor) =>
            {
                if (!OrdersCaller.TryFrom(context.User, out var caller))
                {
                    return OrdersHttp.MissingClaims();
                }

                return (await executor.ExecuteAsync(new GetOrderQuery(id, caller), context.RequestAborted).ConfigureAwait(false)).ToHttpResult();
            })
            .RequirePermission(OrdersPermissions.Read)
            .WithOrdersMetadata("GetOrder", "Returns one order with its lines.")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
