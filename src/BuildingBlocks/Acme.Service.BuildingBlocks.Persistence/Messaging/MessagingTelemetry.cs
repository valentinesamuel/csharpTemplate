using System.Diagnostics;

namespace Acme.Service.BuildingBlocks.Persistence.Messaging;

/// <summary>
/// Names of the outbox/inbox telemetry. The Host adds <see cref="Name"/> as an OpenTelemetry meter and activity
/// source so <c>outbox.dead</c> and the dispatch spans are exported.
/// </summary>
public static class MessagingTelemetry
{
    /// <summary>Meter and activity source name.</summary>
    public const string Name = "Acme.Service.Messaging";

    /// <summary>Counter incremented when a message is marked dead.</summary>
    public const string DeadCounterName = "outbox.dead";

    /// <summary>Name of the activity around the dispatch of one outbox message.</summary>
    public const string DispatchActivityName = "outbox.dispatch";

    internal static ActivitySource ActivitySource { get; } = new(Name);
}
