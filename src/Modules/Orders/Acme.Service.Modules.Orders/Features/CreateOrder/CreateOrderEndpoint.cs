using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.BuildingBlocks.Web.Authorization;
using Acme.Service.BuildingBlocks.Web.Endpoints;
using Acme.Service.BuildingBlocks.Web.Idempotency;
using Acme.Service.BuildingBlocks.Web.Results;
using Acme.Service.Modules.Orders.Contracts;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acme.Service.Modules.Orders.Features.CreateOrder;

/// <summary>
/// <c>POST /api/v1/orders</c> (<c>orders.create</c>): places an order for the caller. 201 with <c>Location</c>. Accepts an
/// <c>Idempotency-Key</c> header: a retry with the same key and body replays the first response without placing a second
/// order; the same key with a different body is 422.
/// </summary>
internal sealed class CreateOrderEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(OrdersHttp.Prefix, async (CreateOrderRequest body, HttpContext context, IUseCaseExecutor<CreateOrderCommand, OrderResponse> executor) =>
            {
                if (!OrdersCaller.TryFrom(context.User, out var caller))
                {
                    return OrdersHttp.MissingClaims();
                }

                var command = new CreateOrderCommand(caller.UserId, body.Currency, body.Lines);
                var result = await executor.ExecuteAsync(command, context.RequestAborted).ConfigureAwait(false);
                return result.ToHttpResult(order => TypedResults.Created(OrdersHttp.PathOf(order.Id), order));
            })
            .RequirePermission(OrdersPermissions.Create)
            .WithIdempotency()
            .WithOrdersMetadata("CreateOrder", "Places a pending order for the caller.")
            .Produces<OrderResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
