using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.GetOrder;

/// <summary>
/// Reads one order (untracked). A missing or deleted order, or another customer's order for a caller without
/// <c>orders.manage</c>, is 404 <c>order_not_found</c>, never 403, so ids cannot be probed.
/// </summary>
internal sealed class GetOrderUseCase(IOrderRepository orders) : IUseCase<GetOrderQuery, OrderResponse>
{
    public async Task<Result<OrderResponse>> ExecuteAsync(GetOrderQuery input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var order = await orders.GetForReadAsync(input.OrderId, cancellationToken).ConfigureAwait(false);
        if (order is null || !input.Caller.MayAccess(order.CustomerId))
        {
            return OrderErrors.NotFound;
        }

        return OrderResponse.From(order);
    }
}
