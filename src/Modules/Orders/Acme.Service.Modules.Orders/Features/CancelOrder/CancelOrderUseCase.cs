using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Modules.Orders.Features.CancelOrder;

/// <summary>
/// Pending to Cancelled. 404 <c>order_not_found</c> unless the caller owns it or manages orders; 409
/// <c>invalid_transition</c> unless pending; 409 <c>concurrency_conflict</c> when another request changed it first.
/// </summary>
internal sealed class CancelOrderUseCase(
    IOrderRepository orders,
    [FromKeyedServices(OrdersModuleKey.Value)] IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IUseCase<CancelOrderCommand, OrderResponse>
{
    public async Task<Result<OrderResponse>> ExecuteAsync(CancelOrderCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var order = await orders.GetByIdAsync(input.OrderId, cancellationToken).ConfigureAwait(false);
        if (order is null || !input.Caller.MayAccess(order.CustomerId))
        {
            return OrderErrors.NotFound;
        }

        var cancelled = order.Cancel(timeProvider.GetUtcNow());
        if (cancelled.IsFailure)
        {
            return cancelled.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return OrderResponse.From(order);
    }
}
