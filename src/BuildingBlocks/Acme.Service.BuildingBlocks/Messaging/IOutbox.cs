namespace Acme.Service.BuildingBlocks.Messaging;

/// <summary>
/// The producing module's transactional outbox. Registered as a keyed scoped service keyed by module
/// name (D-04). Enqueued events are written in the same transaction as the business change (INV-08);
/// use cases never publish to the event bus directly.
/// </summary>
public interface IOutbox
{
    /// <summary>Adds <paramref name="integrationEvent"/> to the current unit of work.</summary>
    void Enqueue(IIntegrationEvent integrationEvent);
}
