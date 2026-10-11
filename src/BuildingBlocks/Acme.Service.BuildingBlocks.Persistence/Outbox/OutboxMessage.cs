namespace Acme.Service.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// A row of the producing module's <c>outbox_messages</c> table (INV-08). Written in the same transaction as
/// the business change by <see cref="EfOutbox{TContext}"/>; delivered by <see cref="OutboxDispatcher{TContext}"/>.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>The integration event id (Guid v7). Also the inbox de-duplication key and the dispatch order.</summary>
    public Guid Id { get; init; }

    /// <summary>The stable event name from <c>[IntegrationEvent]</c> (INV-19), never a CLR type name.</summary>
    public required string Type { get; init; }

    /// <summary>The serialized event (<c>jsonb</c>). Never logged (INV-28).</summary>
    public required string Payload { get; init; }

    /// <summary>When the event occurred (UTC).</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Correlation id of the enqueuing operation (D-25), restored while the event is dispatched.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>W3C <c>traceparent</c> of the enqueuing operation, used as the parent of the dispatch activity.</summary>
    public string? Traceparent { get; init; }

    /// <summary>Failed delivery attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>The message is not dispatched before this time (exponential backoff after a failure).</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>When every handler succeeded; null while pending.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>When the message was given up (MaxAttempts reached, or unknown type); null otherwise.</summary>
    public DateTimeOffset? DeadAt { get; set; }

    /// <summary>Exception type and message of the last failure (truncated); never the payload.</summary>
    public string? LastError { get; set; }
}
