using System.Text.Json;
using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Persistence.Messaging;
using Acme.Service.BuildingBlocks.Tests.Messaging.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Messaging;

public sealed class IntegrationEventTypeRegistryTests
{
    [Fact]
    public void FromTypes_TwoTypesWithSameName_ThrowsNamingBothTypes()
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => IntegrationEventTypeRegistry.FromTypes([typeof(FirstV1), typeof(DuplicateOfFirstV1)]));

        exception.Message.ShouldContain("'tests.first.v1'");
        exception.Message.ShouldContain(nameof(FirstV1));
        exception.Message.ShouldContain(nameof(DuplicateOfFirstV1));
    }

    [Fact]
    public void FromTypes_EventWithoutAttribute_Throws()
    {
        Should.Throw<InvalidOperationException>(() => IntegrationEventTypeRegistry.FromTypes([typeof(UnnamedEvent)]))
            .Message.ShouldContain("INV-19");
    }

    [Fact]
    public void FromTypes_AttributeOnNonEventType_Throws()
    {
        Should.Throw<InvalidOperationException>(() => IntegrationEventTypeRegistry.FromTypes([typeof(NotAnEvent)]))
            .Message.ShouldContain(nameof(IIntegrationEvent));
    }

    [Fact]
    public void FromTypes_SameTypeTwice_RegistersOnce()
    {
        var registry = IntegrationEventTypeRegistry.FromTypes([typeof(FirstV1), typeof(FirstV1)]);

        registry.Names.ShouldBe(["tests.first.v1"]);
    }

    [Fact]
    public void FromAssemblies_TestAssembly_FindsPublicEventsByStableName()
    {
        var registry = IntegrationEventTypeRegistry.FromAssemblies([typeof(WidgetOrderedV1).Assembly, typeof(WidgetOrderedV1).Assembly]);

        registry.TryGetType("widgets.widget-ordered.v1", out var type).ShouldBeTrue();
        type.ShouldBe(typeof(WidgetOrderedV1));
        registry.GetName(typeof(WidgetOrderedV1)).ShouldBe("widgets.widget-ordered.v1");
        registry.TryGetType("widgets.unknown.v1", out _).ShouldBeFalse();
    }

    [Fact]
    public void AddIntegrationEventTypes_InternalEvent_RegisteredAlongsideContractsAssembly()
    {
        var services = new ServiceCollection();
        services.AddIntegrationEvents(typeof(WidgetOrderedV1).Assembly);
        services.AddIntegrationEventTypes(typeof(ModuleInternalV1), typeof(ModuleInternalV1));
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IntegrationEventTypeRegistry>();

        registry.GetName(typeof(ModuleInternalV1)).ShouldBe("tests.module-internal.v1");
        registry.TryGetType("widgets.widget-ordered.v1", out _).ShouldBeTrue();
        var restored = IntegrationEventSerializer.Deserialize(
            typeof(ModuleInternalV1),
            new IntegrationEventSerializer(registry).Serialize(new ModuleInternalV1(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, 7)).Payload);
        restored.ShouldBeOfType<ModuleInternalV1>().Value.ShouldBe(7);
    }

    [Fact]
    public void FromAssemblies_InternalEventType_IsNotScanned()
    {
        var registry = IntegrationEventTypeRegistry.FromAssemblies([typeof(ModuleInternalV1).Assembly]);

        Should.Throw<InvalidOperationException>(() => registry.GetName(typeof(ModuleInternalV1)));
    }

    [Fact]
    public void GetName_UnregisteredType_ThrowsNamingTheType()
    {
        var registry = IntegrationEventTypeRegistry.FromTypes([]);

        Should.Throw<InvalidOperationException>(() => registry.GetName(typeof(FirstV1))).Message.ShouldContain(nameof(FirstV1));
    }

    [Fact]
    public void Serialize_ThenDeserialize_RoundTripsWithStableNameCamelCaseAndStringEnums()
    {
        var registry = IntegrationEventTypeRegistry.FromTypes([typeof(WidgetOrderedV1)]);
        var serializer = new IntegrationEventSerializer(registry);
        var original = new WidgetOrderedV1(Guid.CreateVersion7(), new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), "gear", 3, WidgetColor.Red);

        var (type, payload) = serializer.Serialize(original);
        registry.TryGetType(type, out var eventType).ShouldBeTrue();
        var restored = IntegrationEventSerializer.Deserialize(eventType, payload);

        type.ShouldBe("widgets.widget-ordered.v1");
        payload.ShouldContain("\"widgetName\":\"gear\"");
        payload.ShouldContain("\"color\":\"Red\"");
        restored.ShouldBe(original);
    }

    [Fact]
    public void Serialize_UnregisteredEvent_Throws()
    {
        var serializer = new IntegrationEventSerializer(IntegrationEventTypeRegistry.FromTypes([]));

        Should.Throw<InvalidOperationException>(() => serializer.Serialize(WidgetOrderedV1.Create("gear")));
    }

    [Fact]
    public void Deserialize_NullPayload_ThrowsJsonException()
    {
        Should.Throw<JsonException>(() => IntegrationEventSerializer.Deserialize(typeof(WidgetOrderedV1), "null"));
    }

    [IntegrationEvent("tests.first.v1")]
    private sealed record FirstV1(Guid Id, DateTimeOffset OccurredAt) : IIntegrationEvent;

    [IntegrationEvent("tests.first.v1")]
    private sealed record DuplicateOfFirstV1(Guid Id, DateTimeOffset OccurredAt) : IIntegrationEvent;

    private sealed record UnnamedEvent(Guid Id, DateTimeOffset OccurredAt) : IIntegrationEvent;

    [IntegrationEvent("tests.not-an-event.v1")]
    private sealed class NotAnEvent;
}

/// <summary>An event a module produces and consumes itself, so it stays internal (INV-02).</summary>
[IntegrationEvent("tests.module-internal.v1")]
internal sealed record ModuleInternalV1(Guid Id, DateTimeOffset OccurredAt, int Value) : IIntegrationEvent;
