using Acme.Service.BuildingBlocks.Messaging;

namespace Acme.Service.BuildingBlocks.Persistence.Messaging;

/// <summary>
/// One consumer of one event type, registered by
/// <see cref="MessagingServiceCollectionExtensions.AddIntegrationEventHandler{TEvent,THandler}"/>. The invoker
/// always runs the handler through <see cref="Inbox.InboxHandlerDecorator{TEvent}"/> (INV-09).
/// </summary>
/// <param name="EventType">The event type handled.</param>
/// <param name="ModuleKey">The consuming module (whose unit of work and inbox are used).</param>
/// <param name="ConsumerName">The stable consumer name stored in <c>inbox_messages</c>.</param>
/// <param name="Invoke">Runs the decorated handler with services from the given scope.</param>
internal sealed record IntegrationEventSubscription(
    Type EventType,
    string ModuleKey,
    string ConsumerName,
    Func<IServiceProvider, IIntegrationEvent, CancellationToken, Task> Invoke);

/// <summary>A Contracts assembly whose integration events are added to the <see cref="IntegrationEventTypeRegistry"/>.</summary>
/// <param name="Assembly">The assembly.</param>
internal sealed record IntegrationEventAssembly(System.Reflection.Assembly Assembly);

/// <summary>An explicitly registered (typically module-internal) event type added to the <see cref="IntegrationEventTypeRegistry"/>.</summary>
/// <param name="Type">The event type.</param>
internal sealed record IntegrationEventType(Type Type);
