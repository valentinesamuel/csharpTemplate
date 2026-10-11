using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Modules.Orders.Features.ConfirmOrder;

/// <summary>
/// Pending to Confirmed. 404 <c>order_not_found</c>; 409 <c>invalid_transition</c> unless pending; 409
/// <c>concurrency_conflict</c> when another request changed the order first.
/// </summary>
internal sealed class ConfirmOrderUseCase(
    IOrderRepository orders,
    [FromKeyedServices(OrdersModuleKey.Value)] IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IUseCase<ConfirmOrderCommand, OrderResponse>
{
    public async Task<Result<OrderResponse>> ExecuteAsync(ConfirmOrderCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var order = await orders.GetByIdAsync(input.OrderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        var confirmed = order.Confirm(timeProvider.GetUtcNow());
        if (confirmed.IsFailure)
        {
            return confirmed.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return OrderResponse.From(order);
    }
}
