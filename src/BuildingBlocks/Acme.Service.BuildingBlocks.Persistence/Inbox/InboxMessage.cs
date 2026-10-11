namespace Acme.Service.BuildingBlocks.Persistence.Inbox;

/// <summary>
/// A row of the consuming module's <c>inbox_messages</c> table: records that <see cref="Consumer"/> has
/// handled the integration event <see cref="MessageId"/> (INV-09). Written by
/// <see cref="InboxHandlerDecorator{TEvent}"/> in the consumer's transaction, before the handler runs.
/// </summary>
public sealed class InboxMessage
{
    /// <summary>The integration event id.</summary>
    public Guid MessageId { get; init; }

    /// <summary>The stable consumer name (one per handler registration).</summary>
    public required string Consumer { get; init; }

    /// <summary>When the consumer handled the event.</summary>
    public DateTimeOffset ProcessedAt { get; init; }
}
