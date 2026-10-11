using System.Diagnostics;
using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Persistence.Messaging;

namespace Acme.Service.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// The module's transactional <see cref="IOutbox"/> (INV-08), registered keyed by module name (D-04).
/// <see cref="Enqueue"/> only adds an <see cref="OutboxMessage"/> to the module context; it is written by the
/// same <c>SaveChanges</c> as the business change, inside the unit-of-work transaction, so both commit or
/// neither does.
/// </summary>
/// <remarks>
/// The row carries the ambient correlation id (<see cref="ICorrelationContext"/>, falling back to the current
/// W3C trace id) and the current <c>traceparent</c>, restored when the message is dispatched (D-25).
/// </remarks>
/// <typeparam name="TContext">The producing module's context.</typeparam>
internal sealed class EfOutbox<TContext>(
    TContext context,
    IntegrationEventSerializer serializer,
    TimeProvider timeProvider,
    ICorrelationContext? correlation) : IOutbox
    where TContext : ModuleDbContext
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException">The event has an empty id.</exception>
    /// <exception cref="InvalidOperationException">The event type is not registered (see <c>AddIntegrationEvents</c>).</exception>
    public void Enqueue(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (integrationEvent.Id == Guid.Empty)
        {
            throw new ArgumentException("An integration event must have a non-empty Id (Guid v7).", nameof(integrationEvent));
        }

        var (type, payload) = serializer.Serialize(integrationEvent);
        var activity = Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current : null;

        context.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = integrationEvent.Id,
            Type = type,
            Payload = payload,
            OccurredAt = integrationEvent.OccurredAt.ToUniversalTime(),
            CorrelationId = correlation?.CorrelationId ?? activity?.TraceId.ToHexString(),
            Traceparent = activity?.Id,
            Attempts = 0,
            NextAttemptAt = timeProvider.GetUtcNow(),
        });
    }
}
