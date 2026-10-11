using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Web.Correlation;

namespace Acme.Service.Host.Messaging;

/// <summary>
/// Exposes the web correlation id (<see cref="ICorrelationIdAccessor"/>) as the infrastructure-neutral
/// <see cref="ICorrelationContext"/>, so outbox rows carry the request's correlation id and consumers see it
/// again when the event is dispatched (D-25). BuildingBlocks.Persistence cannot reference BuildingBlocks.Web.
/// </summary>
internal sealed class CorrelationContextBridge(ICorrelationIdAccessor accessor) : ICorrelationContext
{
    public string? CorrelationId
    {
        get => accessor.CorrelationId;
        set => accessor.CorrelationId = value;
    }
}
