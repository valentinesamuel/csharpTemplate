using Acme.Service.BuildingBlocks.Messaging;

namespace Acme.Service.BuildingBlocks.Tests.Messaging.Fixtures;

/// <summary>Enum carried by the test event; serialized as a string.</summary>
public enum WidgetColor
{
    Red,
    Blue,
}

/// <summary>Test integration event; public so that assembly scanning finds it.</summary>
[IntegrationEvent("widgets.widget-ordered.v1")]
public sealed record WidgetOrderedV1(Guid Id, DateTimeOffset OccurredAt, string WidgetName, int Amount, WidgetColor Color) : IIntegrationEvent
{
    public static WidgetOrderedV1 Create(string widgetName, int amount = 1) =>
        new(Guid.CreateVersion7(), TimeProvider.System.GetUtcNow(), widgetName, amount, WidgetColor.Blue);
}
