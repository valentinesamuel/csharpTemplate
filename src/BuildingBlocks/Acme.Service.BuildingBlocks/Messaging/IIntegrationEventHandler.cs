namespace Acme.Service.BuildingBlocks.Messaging;

/// <summary>
/// Consumes an integration event. Delivery is at least once; every handler runs through the inbox
/// de-duplication decorator inside the consumer's unit of work (INV-09).
/// </summary>
/// <typeparam name="TEvent">Event type.</typeparam>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    /// <summary>Handles the event.</summary>
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
