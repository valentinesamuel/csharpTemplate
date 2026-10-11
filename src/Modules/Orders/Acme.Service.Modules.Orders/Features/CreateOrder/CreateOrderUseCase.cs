using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Contracts.IntegrationEvents;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Modules.Orders.Features.CreateOrder;

/// <summary>
/// Places a pending order owned by the caller and enqueues <see cref="OrderPlacedV1"/> in the Orders outbox, so the order
/// and the event commit in the same transaction (INV-08). The order is saved inside the command's transaction before the
/// response is built, so the response carries the stored concurrency token.
/// </summary>
internal sealed class CreateOrderUseCase(
    IOrderRepository orders,
    [FromKeyedServices(OrdersModuleKey.Value)] IUnitOfWork unitOfWork,
    [FromKeyedServices(OrdersModuleKey.Value)] IOutbox outbox,
    TimeProvider timeProvider) : IUseCase<CreateOrderCommand, OrderResponse>
{
    public async Task<Result<OrderResponse>> ExecuteAsync(CreateOrderCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = timeProvider.GetUtcNow();
        var order = Order.Place(input.CustomerId, input.Currency!, OrderLineRequest.ToDrafts(input.Lines!), now);
        await orders.AddAsync(order, cancellationToken).ConfigureAwait(false);
        outbox.Enqueue(new OrderPlacedV1(Guid.CreateVersion7(now), now, order.Id, order.CustomerId, order.Currency, order.Total));
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return OrderResponse.From(order);
    }
}
