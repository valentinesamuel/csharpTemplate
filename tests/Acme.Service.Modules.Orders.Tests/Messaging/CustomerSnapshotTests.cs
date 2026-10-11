using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.Modules.Auth.Contracts.IntegrationEvents;
using Acme.Service.Modules.Orders.IntegrationEventHandlers;
using Acme.Service.Modules.Orders.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Messaging;

/// <summary>
/// P17 deliverable 3: Auth's <c>UserRegisteredV1</c>, consumed by the Orders <c>UserRegisteredHandler</c> through the
/// Orders inbox, upserts <c>orders.customer_snapshots</c> (INV-06 local read model); duplicates are applied once (INV-09).
/// </summary>
[Collection(OrdersDatabaseGroup.Name)]
public sealed class CustomerSnapshotTests(PostgresFixture postgres)
{
    private const string SnapshotEmail = "SELECT email FROM orders.customer_snapshots WHERE id = @id";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RegisterInAuth_CreatesCustomerSnapshotInOrdersSchema()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);

        var user = await host.UserAsync("snapshot@example.com", "Customer");

        await OrdersTestHost.EventuallyAsync(
            async () => await host.ScalarAsync<long>("SELECT count(*) FROM orders.customer_snapshots WHERE id = @id", ("id", user.Id)) == 1,
            "the customer snapshot row appears");
        (await host.ScalarAsync<string>(SnapshotEmail, ("id", user.Id))).ShouldBe("snapshot@example.com");
        (await host.ScalarAsync<long>(
            "SELECT count(*) FROM orders.inbox_messages WHERE consumer = @consumer",
            ("consumer", UserRegisteredHandler.ConsumerName))).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UserRegistered_DuplicateDelivery_IsAppliedOnce()
    {
        await using var host = await OrdersTestHost.CreateAsync(postgres);
        var bus = host.Factory.Services.GetRequiredService<IEventBus>();
        var userId = Guid.CreateVersion7();
        var registered = new UserRegisteredV1(Guid.CreateVersion7(), DateTimeOffset.UtcNow, userId, "first@example.com", "First");

        await bus.PublishAsync(registered, Ct);
        (await host.ScalarAsync<string>(SnapshotEmail, ("id", userId))).ShouldBe("first@example.com");

        // Mark the row, then deliver the same message again: the inbox skips the handler, so the mark survives.
        await host.ScalarAsync<int>("UPDATE orders.customer_snapshots SET email = 'marked@example.com' WHERE id = @id", ("id", userId));
        await bus.PublishAsync(registered, Ct);

        (await host.ScalarAsync<string>(SnapshotEmail, ("id", userId))).ShouldBe("marked@example.com");
        (await host.ScalarAsync<long>(
            "SELECT count(*) FROM orders.inbox_messages WHERE message_id = @id AND consumer = @consumer",
            ("id", registered.Id),
            ("consumer", UserRegisteredHandler.ConsumerName))).ShouldBe(1);
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.customer_snapshots WHERE id = @id", ("id", userId))).ShouldBe(1);

        // A different, newer message for the same user is an upsert of the existing row.
        var newer = registered with { Id = Guid.CreateVersion7(), OccurredAt = registered.OccurredAt.AddMinutes(1), Email = "second@example.com" };
        await bus.PublishAsync(newer, Ct);
        (await host.ScalarAsync<string>(SnapshotEmail, ("id", userId))).ShouldBe("second@example.com");
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.customer_snapshots WHERE id = @id", ("id", userId))).ShouldBe(1);
    }
}
