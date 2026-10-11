using System.Diagnostics.Metrics;
using Acme.Service.BuildingBlocks.Persistence.Messaging;

namespace Acme.Service.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// Outbox metrics (meter <see cref="MessagingTelemetry.Name"/>). Uses the host's <see cref="IMeterFactory"/>
/// when one is registered, otherwise owns its meter.
/// </summary>
internal sealed class OutboxMetrics : IDisposable
{
    private readonly Meter? _ownedMeter;
    private readonly Counter<long> _dead;

    public OutboxMetrics(IMeterFactory? meterFactory)
    {
        Meter = meterFactory?.Create(MessagingTelemetry.Name) ?? (_ownedMeter = new Meter(MessagingTelemetry.Name));
        _dead = Meter.CreateCounter<long>(
            MessagingTelemetry.DeadCounterName,
            unit: "{message}",
            description: "Outbox messages given up after MaxAttempts or because their type is unknown.");
    }

    public Meter Meter { get; }

    public void RecordDead(string module, string eventType) =>
        _dead.Add(1, new KeyValuePair<string, object?>("module", module), new KeyValuePair<string, object?>("event.type", eventType));

    public void Dispose() => _ownedMeter?.Dispose();
}
