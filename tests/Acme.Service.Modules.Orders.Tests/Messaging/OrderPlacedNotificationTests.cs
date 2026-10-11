using Acme.Service.Modules.Orders.IntegrationEventHandlers;
using Acme.Service.Modules.Orders.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Messaging;

/// <summary>
/// The full chain of P17 deliverable 1 and 2: CreateOrder writes <c>OrderPlacedV1</c> to the Orders outbox in its
/// transaction (INV-08), the dispatcher delivers it to <c>OrderPlacedHandler</c> through the inbox (INV-09), which
/// enqueues <c>SendOrderPlacedNotificationJob</c>; the job looks the customer up through Auth's <c>IUserDirectory</c> and
/// emails them.
/// </summary>
[Collection(OrdersDatabaseGroup.Name)]
public sealed class OrderPlacedNotificationTests(PostgresFixture postgres)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateOrder_OutboxDispatchAndJob_EmailsTheCustomerAddress()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("buyer@example.com", "Customer");

        var order = await host.CreateOrderAsync(customer, ("SKU-1", 2, 5m), ("SKU-2", 1, 0.25m));
        var orderId = order.GetProperty("id").GetGuid();

        // The event row was written by the create transaction.
        (await host.ScalarAsync<long>(
            "SELECT count(*) FROM orders.outbox_messages WHERE type = 'orders.order-placed.v1' AND payload::text LIKE @id",
            ("id", $"%{orderId}%"))).ShouldBe(1);

        await OrdersTestHost.EventuallyAsync(
            () => Task.FromResult(host.Factory.Emails.SentTo("buyer@example.com").Any(m => m.Subject.Contains(orderId.ToString(), StringComparison.Ordinal))),
            "the order placed email arrives",
            TimeSpan.FromSeconds(20));

        var email = host.Factory.Emails.SentTo("buyer@example.com").Single(m => m.Subject.Contains(orderId.ToString(), StringComparison.Ordinal));
        email.TextBody.ShouldContain("10.25 EUR");
        (await host.ScalarAsync<bool>(
            "SELECT processed_at IS NOT NULL FROM orders.outbox_messages WHERE type = 'orders.order-placed.v1'")).ShouldBeTrue();
        (await host.ScalarAsync<long>(
            "SELECT count(*) FROM orders.inbox_messages WHERE consumer = @consumer",
            ("consumer", OrderPlacedHandler.ConsumerName))).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateOrder_InvalidBody_WritesNoOutboxRow()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("buyer@example.com", "Customer");

        using var rejected = await host.SendAsync(HttpMethod.Post, "/api/v1/orders", customer, OrdersTestHost.CreateBody("eur", ("SKU", 1, 1m)));

        rejected.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.outbox_messages")).ShouldBe(0);
    }
}
