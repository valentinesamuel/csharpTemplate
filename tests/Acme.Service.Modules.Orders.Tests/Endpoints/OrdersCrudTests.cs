using System.Net;
using Acme.Service.Modules.Orders.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Endpoints;

/// <summary>
/// The order endpoints with real tokens for each seed role (§0.8): permissions plus ownership, 404 instead of 403 for
/// other customers' orders, the state machine, soft delete and the error model (INV-18).
/// </summary>
[Collection(OrdersDatabaseGroup.Name)]
public sealed class OrdersCrudTests(PostgresFixture postgres)
{
    private const string Orders = "/api/v1/orders";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Migrate_CreatesOrdersSchemaAndSeedsOrdersPermissions()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);

        (await host.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'orders')")).ShouldBeTrue();
        (await host.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'orders' AND table_name IN ('orders', 'order_lines', 'outbox_messages', 'inbox_messages', '__ef_migrations_history')"))
            .ShouldBe(5);
        (await host.ScalarAsync<string>(
            "SELECT string_agg(p.name, ',' ORDER BY p.name) FROM auth.role_permissions rp JOIN auth.roles r ON r.id = rp.role_id JOIN auth.permissions p ON p.name = rp.permission_name WHERE r.name = 'Customer' AND p.module = 'orders'"))
            .ShouldBe("orders.cancel,orders.create,orders.read");
        (await host.ScalarAsync<string>(
            "SELECT string_agg(p.name, ',' ORDER BY p.name) FROM auth.role_permissions rp JOIN auth.roles r ON r.id = rp.role_id JOIN auth.permissions p ON p.name = rp.permission_name WHERE r.name = 'Staff' AND p.module = 'orders'"))
            .ShouldBe("orders.manage,orders.read");
        (await host.ScalarAsync<long>(
            "SELECT count(*) FROM auth.role_permissions rp JOIN auth.roles r ON r.id = rp.role_id JOIN auth.permissions p ON p.name = rp.permission_name WHERE r.name = 'Admin' AND p.module = 'orders'"))
            .ShouldBe(4);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Create_Customer_Is201WithLocationAndStoredOrder()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");

        using var created = await host.SendAsync(HttpMethod.Post, Orders, customer, OrdersTestHost.CreateBody("EUR", ("SKU-1", 2, 10.25m), ("SKU-2", 1, 0.5m)));

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var order = await OrdersTestHost.JsonAsync(created);
        var id = order.GetProperty("id").GetGuid();
        created.Headers.Location!.ToString().ShouldBe($"{Orders}/{id}");
        order.GetProperty("customerId").GetGuid().ShouldBe(customer.Id);
        order.GetProperty("status").GetString().ShouldBe("Pending");
        order.GetProperty("currency").GetString().ShouldBe("EUR");
        order.GetProperty("total").GetDecimal().ShouldBe(21.00m);
        order.GetProperty("version").GetUInt32().ShouldBeGreaterThan(0u);
        order.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("sku").GetString()).ShouldBe(["SKU-1", "SKU-2"]);

        using var read = await host.SendAsync(HttpMethod.Get, created.Headers.Location.ToString(), customer);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await OrdersTestHost.JsonAsync(read);
        stored.GetProperty("total").GetDecimal().ShouldBe(21.00m);
        stored.GetProperty("version").GetUInt32().ShouldBe(order.GetProperty("version").GetUInt32());
        stored.GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal().ShouldBe(10.25m);
        (await host.ScalarAsync<decimal>("SELECT total FROM orders.orders WHERE id = @id", ("id", id))).ShouldBe(21.00m);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Endpoints_WithoutToken_Are401Problem()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var id = Guid.CreateVersion7();

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Post, Orders), (HttpMethod.Get, Orders), (HttpMethod.Get, $"{Orders}/{id}"), (HttpMethod.Put, $"{Orders}/{id}"),
            (HttpMethod.Post, $"{Orders}/{id}/confirm"), (HttpMethod.Post, $"{Orders}/{id}/cancel"), (HttpMethod.Delete, $"{Orders}/{id}"),
        })
        {
            using var response = await host.SendAsync(method, path, user: null);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{method} {path}");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Endpoints_MissingPermission_Are403()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var staff = await host.UserAsync("staff@example.com", "Staff");
        var order = await host.CreateOrderAsync(customer, ("SKU", 1, 1m));
        var path = $"{Orders}/{order.GetProperty("id").GetGuid()}";

        using var confirm = await host.SendAsync(HttpMethod.Post, $"{path}/confirm", customer);
        using var delete = await host.SendAsync(HttpMethod.Delete, path, customer);
        using var staffCreate = await host.SendAsync(HttpMethod.Post, Orders, staff, OrdersTestHost.CreateBody("EUR", ("SKU", 1, 1m)));

        confirm.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await OrdersTestHost.ProblemCodeAsync(confirm)).ShouldBe("forbidden");
        delete.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        staffCreate.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdateAndCancel_OwnerWithoutCreateOrCancelPermission_Are403()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var order = await host.CreateOrderAsync(customer, ("SKU", 1, 1m));
        var path = $"{Orders}/{order.GetProperty("id").GetGuid()}";

        // Take orders.create and orders.cancel away from the Customer role; a new token then carries only orders.read.
        await host.ScalarAsync<int>(
            "WITH removed AS (DELETE FROM auth.role_permissions rp USING auth.roles r WHERE r.id = rp.role_id AND r.name = 'Customer' AND rp.permission_name IN ('orders.create', 'orders.cancel') RETURNING 1) SELECT count(*)::int FROM removed");
        var readOnly = await host.UserAsync("reader@example.com", "Customer");
        var readOnlyOrder = (await host.ScalarAsync<Guid>(
            "INSERT INTO orders.orders (id, customer_id, status, currency, total, created_at, updated_at) VALUES (@id, @customer, 'Pending', 'EUR', 0, now(), now()) RETURNING id",
            ("id", Guid.CreateVersion7()),
            ("customer", readOnly.Id))).ToString();

        using var put = await host.SendAsync(HttpMethod.Put, $"{Orders}/{readOnlyOrder}", readOnly, OrdersTestHost.LinesBody(1, ("SKU", 1, 1m)));
        using var cancel = await host.SendAsync(HttpMethod.Post, $"{Orders}/{readOnlyOrder}/cancel", readOnly);
        using var get = await host.SendAsync(HttpMethod.Get, $"{Orders}/{readOnlyOrder}", readOnly);
        using var otherGet = await host.SendAsync(HttpMethod.Get, path, readOnly);

        put.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await OrdersTestHost.ProblemCodeAsync(put)).ShouldBe("forbidden");
        cancel.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        otherGet.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OtherCustomersOrder_Is404ForCustomer_VisibleToStaffAndAdmin()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var owner = await host.UserAsync("owner@example.com", "Customer");
        var other = await host.UserAsync("other@example.com", "Customer");
        var staff = await host.UserAsync("staff@example.com", "Staff");
        var admin = await host.UserAsync("admin@example.com", "Admin");
        var order = await host.CreateOrderAsync(owner, ("SKU", 1, 1m));
        var path = $"{Orders}/{order.GetProperty("id").GetGuid()}";
        var version = order.GetProperty("version").GetUInt32();

        using var get = await host.SendAsync(HttpMethod.Get, path, other);
        using var put = await host.SendAsync(HttpMethod.Put, path, other, OrdersTestHost.LinesBody(version, ("X", 1, 1m)));
        using var cancel = await host.SendAsync(HttpMethod.Post, $"{path}/cancel", other);
        using var unknown = await host.SendAsync(HttpMethod.Get, $"{Orders}/{Guid.CreateVersion7()}", owner);
        using var asStaff = await host.SendAsync(HttpMethod.Get, path, staff);
        using var asAdmin = await host.SendAsync(HttpMethod.Get, path, admin);

        foreach (var response in new[] { get, put, cancel, unknown })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await OrdersTestHost.ProblemCodeAsync(response)).ShouldBe("order_not_found");
        }

        asStaff.StatusCode.ShouldBe(HttpStatusCode.OK);
        asAdmin.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.ScalarAsync<string>("SELECT status FROM orders.orders WHERE id = @id", ("id", order.GetProperty("id").GetGuid()))).ShouldBe("Pending");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Lifecycle_UpdateThenConfirm_FinalOrderRejectsChanges()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var staff = await host.UserAsync("staff@example.com", "Staff");
        var order = await host.CreateOrderAsync(customer, ("SKU-1", 1, 1m));
        var path = $"{Orders}/{order.GetProperty("id").GetGuid()}";

        using var updated = await host.SendAsync(
            HttpMethod.Put, path, customer, OrdersTestHost.LinesBody(order.GetProperty("version").GetUInt32(), ("SKU-9", 3, 2.5m), ("SKU-8", 1, 0.25m)));
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));
        var afterUpdate = await OrdersTestHost.JsonAsync(updated);
        afterUpdate.GetProperty("total").GetDecimal().ShouldBe(7.75m);
        afterUpdate.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("sku").GetString()).ShouldBe(["SKU-9", "SKU-8"]);
        afterUpdate.GetProperty("version").GetUInt32().ShouldNotBe(order.GetProperty("version").GetUInt32());
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.order_lines WHERE order_id = @id", ("id", order.GetProperty("id").GetGuid()))).ShouldBe(2);

        using var confirmed = await host.SendAsync(HttpMethod.Post, $"{path}/confirm", staff);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync(Ct));
        var afterConfirm = await OrdersTestHost.JsonAsync(confirmed);
        afterConfirm.GetProperty("status").GetString().ShouldBe("Confirmed");

        using var cancel = await host.SendAsync(HttpMethod.Post, $"{path}/cancel", customer);
        using var confirmAgain = await host.SendAsync(HttpMethod.Post, $"{path}/confirm", staff);
        using var change = await host.SendAsync(
            HttpMethod.Put, path, customer, OrdersTestHost.LinesBody(afterConfirm.GetProperty("version").GetUInt32(), ("SKU", 1, 1m)));

        foreach (var response in new[] { cancel, confirmAgain, change })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await OrdersTestHost.ProblemCodeAsync(response)).ShouldBe("invalid_transition");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Cancel_OwnerOrStaff_CancelsPendingOrder()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var staff = await host.UserAsync("staff@example.com", "Staff");
        var first = await host.CreateOrderAsync(customer, ("SKU", 1, 1m));
        var second = await host.CreateOrderAsync(customer, ("SKU", 1, 1m));

        using var byOwner = await host.SendAsync(HttpMethod.Post, $"{Orders}/{first.GetProperty("id").GetGuid()}/cancel", customer);
        using var byStaff = await host.SendAsync(HttpMethod.Post, $"{Orders}/{second.GetProperty("id").GetGuid()}/cancel", staff);

        byOwner.StatusCode.ShouldBe(HttpStatusCode.OK, await byOwner.Content.ReadAsStringAsync(Ct));
        (await OrdersTestHost.JsonAsync(byOwner)).GetProperty("status").GetString().ShouldBe("Cancelled");
        byStaff.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await OrdersTestHost.JsonAsync(byStaff)).GetProperty("status").GetString().ShouldBe("Cancelled");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Update_StaffChangesCustomersOrder_IsAllowed()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var staff = await host.UserAsync("staff@example.com", "Staff");
        var order = await host.CreateOrderAsync(customer, ("SKU", 1, 1m));

        using var response = await host.SendAsync(
            HttpMethod.Put, $"{Orders}/{order.GetProperty("id").GetGuid()}", staff, OrdersTestHost.LinesBody(order.GetProperty("version").GetUInt32(), ("NEW", 2, 3m)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await OrdersTestHost.JsonAsync(response);
        body.GetProperty("total").GetDecimal().ShouldBe(6m);
        body.GetProperty("customerId").GetGuid().ShouldBe(customer.Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Delete_Admin_SoftDeletesAndHidesTheOrder()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var admin = await host.UserAsync("admin@example.com", "Admin");
        var order = await host.CreateOrderAsync(customer, ("SKU", 1, 1m));
        var id = order.GetProperty("id").GetGuid();
        var path = $"{Orders}/{id}";

        using (var delete = await host.SendAsync(HttpMethod.Delete, path, admin))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.NoContent, await delete.Content.ReadAsStringAsync(Ct));
        }

        using var get = await host.SendAsync(HttpMethod.Get, path, admin);
        using var deleteAgain = await host.SendAsync(HttpMethod.Delete, path, admin);
        using var cancel = await host.SendAsync(HttpMethod.Post, $"{path}/cancel", customer);
        using var list = await host.SendAsync(HttpMethod.Get, Orders, customer);

        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        deleteAgain.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        cancel.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await OrdersTestHost.JsonAsync(list)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await host.ScalarAsync<bool>("SELECT deleted_at IS NOT NULL FROM orders.orders WHERE id = @id", ("id", id))).ShouldBeTrue();
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.order_lines WHERE order_id = @id", ("id", id))).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Create_InvalidBody_Is400ValidationProblem()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var customer = await host.UserAsync("customer@example.com", "Customer");

        using var badCurrency = await host.SendAsync(HttpMethod.Post, Orders, customer, OrdersTestHost.CreateBody("euro", ("SKU", 1, 1m)));
        using var noLines = await host.SendAsync(HttpMethod.Post, Orders, customer, OrdersTestHost.CreateBody("EUR"));
        using var badLine = await host.SendAsync(HttpMethod.Post, Orders, customer, OrdersTestHost.CreateBody("EUR", ("SKU", 0, 1.001m)));

        foreach (var response in new[] { badCurrency, noLines, badLine })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await OrdersTestHost.ProblemCodeAsync(response)).ShouldBe("validation_failed");
        }

        var errors = (await OrdersTestHost.JsonAsync(badLine)).GetProperty("errors");
        errors.TryGetProperty("Lines[0].Quantity", out _).ShouldBeTrue();
        errors.TryGetProperty("Lines[0].UnitPrice", out _).ShouldBeTrue();
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.orders")).ShouldBe(0);
    }
}
