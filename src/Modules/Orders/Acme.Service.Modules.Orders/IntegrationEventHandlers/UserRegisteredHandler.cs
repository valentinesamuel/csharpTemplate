using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.Modules.Auth.Contracts.IntegrationEvents;
using Acme.Service.Modules.Orders.Domain;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Orders.IntegrationEventHandlers;

/// <summary>
/// Consumes Auth's <see cref="UserRegisteredV1"/> (cross-module, via Auth.Contracts only, INV-01) and upserts the
/// customer's row in <c>orders.customer_snapshots</c>, the local read model that avoids cross-schema joins (INV-06). Runs
/// through the inbox decorator inside the Orders unit of work (INV-07, INV-09): a duplicate delivery is skipped by the
/// inbox, and the changes commit with the inbox row.
/// </summary>
internal sealed partial class UserRegisteredHandler(
    IRepository<CustomerSnapshot, Guid> snapshots,
    ILogger<UserRegisteredHandler> logger) : IIntegrationEventHandler<UserRegisteredV1>
{
    /// <summary>The stable consumer name recorded in <c>orders.inbox_messages</c>.</summary>
    public const string ConsumerName = "orders.customer-snapshot";

    public async Task HandleAsync(UserRegisteredV1 integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var snapshot = await snapshots.GetByIdAsync(integrationEvent.UserId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            await snapshots.AddAsync(
                CustomerSnapshot.Create(integrationEvent.UserId, integrationEvent.Email, integrationEvent.DisplayName, integrationEvent.OccurredAt),
                cancellationToken).ConfigureAwait(false);
            LogCreated(logger, integrationEvent.UserId);
            return;
        }

        if (!snapshot.Apply(integrationEvent.Email, integrationEvent.DisplayName, integrationEvent.OccurredAt))
        {
            LogStale(logger, integrationEvent.UserId, integrationEvent.Id);
            return;
        }

        LogUpdated(logger, integrationEvent.UserId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer snapshot of user {UserId} created")]
    private static partial void LogCreated(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer snapshot of user {UserId} updated")]
    private static partial void LogUpdated(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer snapshot of user {UserId} is newer than message {MessageId}; not changed")]
    private static partial void LogStale(ILogger logger, Guid userId, Guid messageId);
}
