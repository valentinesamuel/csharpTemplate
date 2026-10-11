namespace Acme.Service.BuildingBlocks.Messaging;

/// <summary>
/// An event published by one module for others, delivered at least once via the outbox (INV-08, INV-09).
/// Implementations are <c>sealed record</c>s in a <c>*.Contracts</c> assembly marked with
/// <see cref="IntegrationEventAttribute"/> (INV-19).
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Unique event id (Guid v7); also the inbox de-duplication key.</summary>
    Guid Id { get; }

    /// <summary>When the event occurred (UTC, from <see cref="TimeProvider"/>).</summary>
    DateTimeOffset OccurredAt { get; }
}
