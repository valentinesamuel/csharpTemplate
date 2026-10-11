using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Modules.Orders.Features.UpdateOrderLines;

/// <summary>
/// Replaces the lines of a pending order. 404 <c>order_not_found</c> unless the caller owns it or manages orders;
/// 409 <c>concurrency_conflict</c> when the caller's version is stale, or when another request commits first (the stored
/// row version is checked on save); 409 <c>invalid_transition</c> unless pending.
/// </summary>
internal sealed class UpdateOrderLinesUseCase(
    IOrderRepository orders,
    [FromKeyedServices(OrdersModuleKey.Value)] IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IUseCase<UpdateOrderLinesCommand, OrderResponse>
{
    public async Task<Result<OrderResponse>> ExecuteAsync(UpdateOrderLinesCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var order = await orders.GetByIdAsync(input.OrderId, cancellationToken).ConfigureAwait(false);
        if (order is null || !input.Caller.MayAccess(order.CustomerId))
        {
            return OrderErrors.NotFound;
        }

        if (order.Version != input.Version)
        {
            return OrderErrors.ConcurrencyConflict;
        }

        var replaced = order.ReplaceLines(OrderLineRequest.ToDrafts(input.Lines!), timeProvider.GetUtcNow());
        if (replaced.IsFailure)
        {
            return replaced.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return OrderResponse.From(order);
    }
}
