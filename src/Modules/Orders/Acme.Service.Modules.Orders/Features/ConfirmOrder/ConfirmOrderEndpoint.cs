using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Authorization;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Orders.Contracts;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Orders.Features.ConfirmOrder;

/// <summary><c>POST /api/v1/orders/{id}/confirm</c> (<c>orders.manage</c>): Pending to Confirmed.</summary>
internal sealed class ConfirmOrderEndpoint : IEndpoint
{
    public const string Path = OrdersHttp.ItemPath + "/confirm";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, async (Guid id, HttpContext context, IUseCaseExecutor<ConfirmOrderCommand, OrderResponse> executor) =>
                (await executor.ExecuteAsync(new ConfirmOrderCommand(id), context.RequestAborted).ConfigureAwait(false)).ToHttpResult())
            .RequirePermission(OrdersPermissions.Manage)
            .WithOrdersMetadata("ConfirmOrder", "Confirms a pending order.")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
