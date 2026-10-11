using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.Modules.Orders.Contracts.IntegrationEvents;
using Acme.Service.Modules.Orders.Jobs;

namespace Acme.Service.Modules.Orders.IntegrationEventHandlers;

/// <summary>
/// Turns an <see cref="OrderPlacedV1"/> into a <see cref="SendOrderPlacedNotificationJob"/>. Runs through the inbox
/// decorator inside the Orders unit of work (INV-09); the email is sent by the job, outside any transaction (INV-10).
/// </summary>
internal sealed class OrderPlacedHandler(IJobQueue jobQueue) : IIntegrationEventHandler<OrderPlacedV1>
{
    /// <summary>The stable consumer name recorded in <c>orders.inbox_messages</c>.</summary>
    public const string ConsumerName = "orders.send-order-placed-notification";

    public Task HandleAsync(OrderPlacedV1 integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return jobQueue.EnqueueAsync<SendOrderPlacedNotificationJob, OrderPlacedNotificationPayload>(
            new OrderPlacedNotificationPayload(integrationEvent.OrderId, integrationEvent.CustomerId, integrationEvent.Currency, integrationEvent.Total),
            cancellationToken);
    }
}
