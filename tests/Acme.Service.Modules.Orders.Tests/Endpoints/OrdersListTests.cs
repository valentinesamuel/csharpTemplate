using System.Net;
using System.Text.Json;
using Acme.Service.Modules.Orders.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Endpoints;

/// <summary><c>GET /api/v1/orders</c>: ownership scoping, paging envelope, sort whitelist and filters (§0.5).</summary>
[Collection(OrdersDatabaseGroup.Name)]
public sealed class OrdersListTests(PostgresFixture postgres)
{
    private const string Orders = "/api/v1/orders";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task List_CustomerSeesOwnOrders_StaffSeesAll()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var alice = await host.UserAsync("alice@example.com", "Customer");
        var bob = await host.UserAsync("bob@example.com", "Customer");
        var staff = await host.UserAsync("staff@example.com", "Staff");
        await host.CreateOrderAsync(alice, ("A", 1, 1m));
        await host.CreateOrderAsync(alice, ("A", 1, 2m));
        await host.CreateOrderAsync(bob, ("B", 1, 3m));

        var asAlice = await ListAsync(host, alice, "");
        var asBob = await ListAsync(host, bob, "");
        var asStaff = await ListAsync(host, staff, "");

        asAlice.GetProperty("totalCount").GetInt32().ShouldBe(2);
        Items(asAlice).ShouldAllBe(o => o.GetProperty("customerId").GetGuid() == alice.Id);
        asBob.GetProperty("totalCount").GetInt32().ShouldBe(1);
        asStaff.GetProperty("totalCount").GetInt32().ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task List_Paging_ReturnsEnvelopeNewestFirst()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var ids = new List<Guid>();
        for (var i = 1; i <= 5; i++)
        {
            ids.Add((await host.CreateOrderAsync(customer, ("SKU", i, 1m))).GetProperty("id").GetGuid());
        }

        var first = await ListAsync(host, customer, "?page=1&pageSize=2");
        var last = await ListAsync(host, customer, "?page=3&pageSize=2");
        var beyond = await ListAsync(host, customer, "?page=9&pageSize=2");

        first.GetProperty("page").GetInt32().ShouldBe(1);
        first.GetProperty("pageSize").GetInt32().ShouldBe(2);
        first.GetProperty("totalCount").GetInt32().ShouldBe(5);
        first.GetProperty("totalPages").GetInt32().ShouldBe(3);
        Items(first).Select(o => o.GetProperty("id").GetGuid()).ShouldBe([ids[4], ids[3]]);
        Items(last).Select(o => o.GetProperty("id").GetGuid()).ShouldBe([ids[0]]);
        Items(beyond).ShouldBeEmpty();
        Items(first)[0].TryGetProperty("lineCount", out var lineCount).ShouldBeTrue();
        lineCount.GetInt32().ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task List_SortByTotalAndStatus_FollowsTheRequestedOrder()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var cheap = await host.CreateOrderAsync(customer, ("SKU", 1, 5m));
        var dear = await host.CreateOrderAsync(customer, ("SKU", 1, 50m));
        var middle = await host.CreateOrderAsync(customer, ("SKU", 1, 20m));
        using (var cancel = await host.SendAsync(HttpMethod.Post, $"{Orders}/{dear.GetProperty("id").GetGuid()}/cancel", customer))
        {
            cancel.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var byTotal = await ListAsync(host, customer, "?sort=total");
        var byTotalDescending = await ListAsync(host, customer, "?sort=-total");
        var byStatusThenTotal = await ListAsync(host, customer, "?sort=status,-total");

        Totals(byTotal).ShouldBe([5m, 20m, 50m]);
        Totals(byTotalDescending).ShouldBe([50m, 20m, 5m]);
        Items(byStatusThenTotal).Select(o => o.GetProperty("id").GetGuid())
            .ShouldBe([dear.GetProperty("id").GetGuid(), middle.GetProperty("id").GetGuid(), cheap.GetProperty("id").GetGuid()]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task List_Filters_StatusAndCreatedRange()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var kept = await host.CreateOrderAsync(customer, ("SKU", 1, 1m));
        var cancelled = await host.CreateOrderAsync(customer, ("SKU", 1, 1m));
        using (var cancel = await host.SendAsync(HttpMethod.Post, $"{Orders}/{cancelled.GetProperty("id").GetGuid()}/cancel", customer))
        {
            cancel.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var pending = await ListAsync(host, customer, "?status=Pending");
        var cancelledOnly = await ListAsync(host, customer, "?status=cancelled");
        var future = await ListAsync(host, customer, "?createdFrom=2999-01-01T00:00:00Z");
        var past = await ListAsync(host, customer, "?createdTo=2000-01-01T00:00:00Z");
        var window = await ListAsync(host, customer, "?createdFrom=2000-01-01T00:00:00Z&createdTo=2999-01-01T00:00:00Z&status=Pending");

        Items(pending).Select(o => o.GetProperty("id").GetGuid()).ShouldBe([kept.GetProperty("id").GetGuid()]);
        Items(cancelledOnly).Select(o => o.GetProperty("status").GetString()).ShouldBe(["Cancelled"]);
        future.GetProperty("totalCount").GetInt32().ShouldBe(0);
        past.GetProperty("totalCount").GetInt32().ShouldBe(0);
        window.GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("?sort=customerId", "invalid_sort")]
    [InlineData("?sort=-createdAt,unknown", "invalid_sort")]
    [InlineData("?page=abc", "invalid_paging")]
    [InlineData("?status=Shipped", "invalid_filter")]
    [InlineData("?createdFrom=yesterday", "invalid_filter")]
    [InlineData("?createdFrom=2026-02-01T00:00:00Z&createdTo=2026-01-01T00:00:00Z", "validation_failed")]
    public async Task List_InvalidQuery_Is400(string query, string code)
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");

        using var response = await host.SendAsync(HttpMethod.Get, Orders + query, customer);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await OrdersTestHost.ProblemCodeAsync(response)).ShouldBe(code);
    }

    private static async Task<JsonElement> ListAsync(OrdersTestHost host, TestUser user, string query)
    {
        using var response = await host.SendAsync(HttpMethod.Get, Orders + query, user);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await OrdersTestHost.JsonAsync(response);
    }

    private static List<JsonElement> Items(JsonElement page) => [.. page.GetProperty("items").EnumerateArray()];

    private static List<decimal> Totals(JsonElement page) => [.. Items(page).Select(o => o.GetProperty("total").GetDecimal())];
}
