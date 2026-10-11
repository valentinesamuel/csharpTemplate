namespace Acme.Service.BuildingBlocks.Messaging;

/// <summary>
/// In-process dispatch of an integration event to its registered handlers. Used by the outbox
/// dispatcher only; use cases enqueue through <see cref="IOutbox"/> instead (INV-08).
/// </summary>
public interface IEventBus
{
    /// <summary>Dispatches <paramref name="integrationEvent"/> to every handler of its runtime type.</summary>
    Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken);
}
