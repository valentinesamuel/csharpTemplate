using System.Net;
using System.Net.Http.Json;
using Acme.Service.Modules.Orders.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Endpoints;

/// <summary>P17 deliverable 6: <c>POST /api/v1/orders</c> honours <c>Idempotency-Key</c>.</summary>
[Collection(OrdersDatabaseGroup.Name)]
public sealed class OrdersIdempotencyTests(PostgresFixture postgres)
{
    private const string Orders = "/api/v1/orders";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Create_ReplayedIdempotencyKey_ReturnsTheSameOrderAndStoresOneRow()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var body = OrdersTestHost.CreateBody("EUR", ("SKU-1", 2, 3.5m));

        using var first = await host.SendAsync(HttpMethod.Post, Orders, customer, JsonContent.Create(body), ("Idempotency-Key", "order-key-1"));
        using var replay = await host.SendAsync(HttpMethod.Post, Orders, customer, JsonContent.Create(body), ("Idempotency-Key", "order-key-1"));

        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.Content.ReadAsStringAsync(Ct));
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        var firstBody = await first.Content.ReadAsStringAsync(Ct);
        (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(firstBody);
        replay.Headers.Location.ShouldBe(first.Headers.Location);
        replay.Headers.Contains("Idempotent-Replayed").ShouldBeTrue();
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.orders WHERE customer_id = @id", ("id", customer.Id))).ShouldBe(1);
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.outbox_messages WHERE type = 'orders.order-placed.v1'")).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Create_SameKeyDifferentBody_Is422AndStoresOneRow()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");

        using var first = await host.SendAsync(
            HttpMethod.Post, Orders, customer, JsonContent.Create(OrdersTestHost.CreateBody("EUR", ("SKU-1", 1, 1m))), ("Idempotency-Key", "order-key-2"));
        using var reused = await host.SendAsync(
            HttpMethod.Post, Orders, customer, JsonContent.Create(OrdersTestHost.CreateBody("EUR", ("SKU-2", 9, 1m))), ("Idempotency-Key", "order-key-2"));

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await OrdersTestHost.ProblemCodeAsync(reused)).ShouldBe("idempotency_key_reused");
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.orders WHERE customer_id = @id", ("id", customer.Id))).ShouldBe(1);
    }
}
