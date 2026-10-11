using Acme.Service.BuildingBlocks.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Persistence.Messaging;

/// <summary>
/// In-process <see cref="IEventBus"/> used by the outbox dispatcher (D-05). Delivers an event to every
/// subscription registered for its runtime type, each in its own DI scope and therefore in its own consumer
/// unit of work. Every subscription runs even when an earlier one fails; failures are then rethrown together
/// so the outbox retries the message, and the inbox prevents the consumers that succeeded from being
/// re-applied (INV-09).
/// </summary>
internal sealed partial class InProcessEventBus(
    IEnumerable<IntegrationEventSubscription> subscriptions,
    IServiceScopeFactory scopeFactory,
    ILogger<InProcessEventBus> logger) : IEventBus
{
    private readonly ILookup<Type, IntegrationEventSubscription> _subscriptions = subscriptions.ToLookup(s => s.EventType);

    /// <inheritdoc />
    public async Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var eventType = integrationEvent.GetType();
        var targets = _subscriptions[eventType].ToList();
        if (targets.Count == 0)
        {
            LogNoSubscribers(logger, eventType.Name, integrationEvent.Id);
            return;
        }

        List<Exception>? failures = null;
        foreach (var subscription in targets)
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                try
                {
                    await subscription.Invoke(scope.ServiceProvider, integrationEvent, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    LogConsumerFailed(logger, ex, subscription.ConsumerName, integrationEvent.Id);
                    (failures ??= []).Add(ex);
                }
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                $"{failures.Count} of {targets.Count} consumer(s) failed to handle integration event {integrationEvent.Id}.",
                failures);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No consumer is subscribed to {EventType}; message {MessageId} is complete")]
    private static partial void LogNoSubscribers(ILogger logger, string eventType, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} failed to handle message {MessageId}")]
    private static partial void LogConsumerFailed(ILogger logger, Exception exception, string consumer, Guid messageId);
}
