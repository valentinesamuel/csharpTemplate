using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.DeleteOrder;

/// <summary>
/// Soft-deletes an order: it stays in the table with <c>deleted_at</c> set and disappears from every query. 404
/// <c>order_not_found</c> when missing or already deleted. Saved by the command's unit of work.
/// </summary>
internal sealed class DeleteOrderUseCase(IOrderRepository orders, TimeProvider timeProvider) : IUseCase<DeleteOrderCommand, Done>
{
    public async Task<Result<Done>> ExecuteAsync(DeleteOrderCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var order = await orders.GetByIdAsync(input.OrderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        order.Delete(timeProvider.GetUtcNow());
        return Done.Value;
    }
}
