using System.Text.Json;
using System.Text.Json.Serialization;
using Acme.Service.BuildingBlocks.Messaging;

namespace Acme.Service.BuildingBlocks.Persistence.Messaging;

/// <summary>
/// Serializes integration events for the outbox: the persisted type is the stable registry name (INV-19) and
/// the payload is camelCase JSON with string enums, so renaming a CLR type or reordering an enum does not
/// break stored messages.
/// </summary>
internal sealed class IntegrationEventSerializer(IntegrationEventTypeRegistry registry)
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Returns the stable name and JSON payload of <paramref name="integrationEvent"/>.</summary>
    /// <exception cref="InvalidOperationException">The event type is not registered.</exception>
    public (string Type, string Payload) Serialize(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var eventType = integrationEvent.GetType();
        var name = registry.GetName(eventType);
        return (name, JsonSerializer.Serialize(integrationEvent, eventType, _options));
    }

    /// <summary>Deserializes <paramref name="payload"/> as <paramref name="eventType"/>.</summary>
    /// <exception cref="JsonException">The payload is not a valid event of that type.</exception>
    public static IIntegrationEvent Deserialize(Type eventType, string payload)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(payload);

        return JsonSerializer.Deserialize(payload, eventType, _options) as IIntegrationEvent
            ?? throw new JsonException("The payload deserialized to null or to a type that is not an integration event.");
    }
}
