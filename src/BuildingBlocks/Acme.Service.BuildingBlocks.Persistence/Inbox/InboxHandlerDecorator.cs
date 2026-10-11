using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Persistence.Inbox;

/// <summary>
/// Makes an integration event handler idempotent (INV-09). Runs the handler inside the CONSUMER module's
/// unit of work (INV-07): first inserts <c>inbox_messages(message_id, consumer)</c>; when that row already
/// exists the message was handled before, so the handler is skipped and delivery succeeds. Otherwise the
/// handler runs and its changes commit together with the inbox row, or roll back with it. Use cases the
/// handler executes join the same transaction (nested units of work join, P5).
/// </summary>
/// <remarks>
/// Handlers must not perform external I/O inside this transaction (INV-10); they enqueue a job or an outbox
/// event instead. Registered only through
/// <see cref="Messaging.MessagingServiceCollectionExtensions.AddIntegrationEventHandler{TEvent,THandler}"/>.
/// </remarks>
/// <typeparam name="TEvent">The event type.</typeparam>
internal sealed partial class InboxHandlerDecorator<TEvent>(
    IIntegrationEventHandler<TEvent> inner,
    IUnitOfWork unitOfWork,
    IInboxStore inbox,
    string consumerName,
    TimeProvider timeProvider,
    ILogger logger) : IIntegrationEventHandler<TEvent>
    where TEvent : IIntegrationEvent
{
    /// <summary>The stable consumer name recorded in the inbox.</summary>
    public string ConsumerName { get; } = consumerName;

    /// <summary>Builds the decorator for <typeparamref name="THandler"/> from the services of one dispatch scope.</summary>
    public static InboxHandlerDecorator<TEvent> Create<THandler>(IServiceProvider services, string moduleKey, string consumerName)
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);

        return new InboxHandlerDecorator<TEvent>(
            services.GetRequiredService<THandler>(),
            services.GetKeyedService<IUnitOfWork>(moduleKey) ?? throw MissingModule(moduleKey, consumerName),
            services.GetKeyedService<IInboxStore>(moduleKey) ?? throw MissingModule(moduleKey, consumerName),
            consumerName,
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<InboxHandlerDecorator<TEvent>>>());
    }

    /// <inheritdoc />
    public async Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var result = await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                var firstDelivery = await inbox
                    .TryRecordAsync(integrationEvent.Id, ConsumerName, timeProvider.GetUtcNow(), token)
                    .ConfigureAwait(false);
                if (!firstDelivery)
                {
                    LogDuplicate(logger, ConsumerName, integrationEvent.Id);
                    return Result.Success();
                }

                await inner.HandleAsync(integrationEvent, token).ConfigureAwait(false);
                return Result.Success();
            },
            retryOnTransientFailure: false,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            // The outbox retries the message; the error code (never data) is recorded as its last error.
            throw new InvalidOperationException(
                $"Consumer '{ConsumerName}' could not commit message {integrationEvent.Id}: {result.Error.Code}.");
        }
    }

    private static InvalidOperationException MissingModule(string moduleKey, string consumerName) =>
        new($"Consumer '{consumerName}' needs module '{moduleKey}' persistence. Call AddModuleDbContext with this module key.");

    [LoggerMessage(Level = LogLevel.Debug, Message = "Consumer {Consumer} already handled message {MessageId}; skipped")]
    private static partial void LogDuplicate(ILogger logger, string consumer, Guid messageId);
}
