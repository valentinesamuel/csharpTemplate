using System.Net;
using Acme.Service.Modules.Orders.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Endpoints;

/// <summary>Optimistic concurrency on <c>xmin</c>: stale versions and lost races are 409 <c>concurrency_conflict</c>.</summary>
[Collection(OrdersDatabaseGroup.Name)]
public sealed class OrdersConcurrencyTests(PostgresFixture postgres)
{
    private const string Orders = "/api/v1/orders";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Update_StaleVersion_Is409AndKeepsTheNewerLines()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var order = await host.CreateOrderAsync(customer, ("SKU", 1, 1m));
        var path = $"{Orders}/{order.GetProperty("id").GetGuid()}";
        var original = order.GetProperty("version").GetUInt32();

        using (var first = await host.SendAsync(HttpMethod.Put, path, customer, OrdersTestHost.LinesBody(original, ("FIRST", 1, 2m))))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        }

        using var stale = await host.SendAsync(HttpMethod.Put, path, customer, OrdersTestHost.LinesBody(original, ("STALE", 1, 3m)));
        using var missing = await host.SendAsync(HttpMethod.Put, path, customer, new { lines = new[] { new { sku = "X", quantity = 1, unitPrice = 1m } } });

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrdersTestHost.ProblemCodeAsync(stale)).ShouldBe("concurrency_conflict");
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var read = await host.SendAsync(HttpMethod.Get, path, customer);
        var current = await OrdersTestHost.JsonAsync(read);
        current.GetProperty("lines")[0].GetProperty("sku").GetString().ShouldBe("FIRST");
        current.GetProperty("total").GetDecimal().ShouldBe(2m);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Update_ConcurrentRequestsWithSameVersion_ExactlyOneWins()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var order = await host.CreateOrderAsync(customer, ("SKU", 1, 1m));
        var path = $"{Orders}/{order.GetProperty("id").GetGuid()}";
        var version = order.GetProperty("version").GetUInt32();

        var responses = await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
            host.SendAsync(HttpMethod.Put, path, customer, OrdersTestHost.LinesBody(version, ($"RACE-{i}", i, 1m)))));
        try
        {
            responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            var losers = responses.Where(r => r.StatusCode != HttpStatusCode.OK).ToList();
            losers.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);
            foreach (var loser in losers)
            {
                (await OrdersTestHost.ProblemCodeAsync(loser)).ShouldBe("concurrency_conflict");
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.order_lines WHERE order_id = @id", ("id", order.GetProperty("id").GetGuid()))).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConfirmAndCancel_Concurrently_OnlyOneTransitionApplies()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var staff = await host.UserAsync("staff@example.com", "Staff");
        var order = await host.CreateOrderAsync(customer, ("SKU", 1, 1m));
        var path = $"{Orders}/{order.GetProperty("id").GetGuid()}";

        var confirm = host.SendAsync(HttpMethod.Post, $"{path}/confirm", staff);
        var cancel = host.SendAsync(HttpMethod.Post, $"{path}/cancel", customer);
        using var confirmed = await confirm;
        using var cancelled = await cancel;

        new[] { confirmed.StatusCode, cancelled.StatusCode }.Count(s => s == HttpStatusCode.OK).ShouldBe(1);
        new[] { confirmed.StatusCode, cancelled.StatusCode }.ShouldContain(HttpStatusCode.Conflict);
        var status = await host.ScalarAsync<string>("SELECT status FROM orders.orders WHERE id = @id", ("id", order.GetProperty("id").GetGuid()));
        status.ShouldBe(confirmed.StatusCode == HttpStatusCode.OK ? "Confirmed" : "Cancelled");
    }
}
