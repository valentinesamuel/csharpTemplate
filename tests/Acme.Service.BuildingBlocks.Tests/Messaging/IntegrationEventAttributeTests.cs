using Acme.Service.BuildingBlocks.Messaging;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Messaging;

public sealed class IntegrationEventAttributeTests
{
    [Theory]
    [InlineData("orders.order-placed.v1")]
    [InlineData("auth.user-registered.v12")]
    [InlineData("a1.b-2.v0")]
    public void IsValidName_WellFormedName_ReturnsTrue(string name)
    {
        IntegrationEventAttribute.IsValidName(name).ShouldBeTrue();
        new IntegrationEventAttribute(name).Name.ShouldBe(name);
    }

    [Theory]
    [InlineData("Orders.order-placed.v1")]
    [InlineData("orders.order_placed.v1")]
    [InlineData("orders.OrderPlaced.v1")]
    [InlineData("orders.order-placed")]
    [InlineData("orders.order-placed.1")]
    [InlineData("orders.order-placed.v")]
    [InlineData("orders.order-placed.V1")]
    [InlineData("orders.placed.extra.v1")]
    [InlineData("orders.order-placed.v1\n")]
    [InlineData("")]
    public void IsValidName_MalformedName_ReturnsFalseAndConstructorThrows(string name)
    {
        IntegrationEventAttribute.IsValidName(name).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => new IntegrationEventAttribute(name));
    }

    [Fact]
    public void GetName_AnnotatedType_ReturnsDeclaredName()
    {
        IntegrationEventAttribute.GetName(typeof(SampleEventV1)).ShouldBe("tests.sample-event.v1");
        IntegrationEventAttribute.GetName(typeof(string)).ShouldBeNull();
    }

    [IntegrationEvent("tests.sample-event.v1")]
    private sealed record SampleEventV1(Guid Id, DateTimeOffset OccurredAt) : IIntegrationEvent;
}
