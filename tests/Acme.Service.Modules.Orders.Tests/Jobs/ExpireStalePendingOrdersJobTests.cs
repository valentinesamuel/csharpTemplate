using Acme.Service.BuildingBlocks.Locking;
using Acme.Service.Modules.Orders.Jobs;
using Acme.Service.Modules.Orders.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Jobs;

/// <summary>
/// P17 deliverable 4: the recurring expiry job is guarded by the distributed lock <c>orders:expire-stale</c> (only one of
/// two concurrent runs works) and expires exactly the pending orders older than <c>Orders:PendingExpiry</c>, in batches.
/// </summary>
[Collection(OrdersDatabaseGroup.Name)]
public sealed class ExpireStalePendingOrdersJobTests(PostgresFixture postgres)
{
    private const string StatusOf = "SELECT status FROM orders.orders WHERE id = @id";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_TwoConcurrentInvocations_OnlyOneRunsAndExpiresOnlyStalePendingOrders()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var staff = await host.UserAsync("staff@example.com", "Staff");
        var stale1 = await CreateOrderIdAsync(host, customer);
        var stale2 = await CreateOrderIdAsync(host, customer);
        var fresh = await CreateOrderIdAsync(host, customer);
        var staleConfirmed = await CreateOrderIdAsync(host, customer);
        using (var confirmed = await host.SendAsync(HttpMethod.Post, $"/api/v1/orders/{staleConfirmed}/confirm", staff))
        {
            confirmed.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        }

        await AgeAsync(host, TimeSpan.FromHours(25), stale1, stale2, staleConfirmed);
        await AgeAsync(host, TimeSpan.FromHours(23), fresh);

        var gate = host.Factory.Services.GetRequiredService<GatedLock>();
        gate.Arm();
        using var firstScope = host.Factory.Services.CreateScope();
        using var secondScope = host.Factory.Services.CreateScope();
        var firstJob = firstScope.ServiceProvider.GetRequiredService<ExpireStalePendingOrdersJob>();
        var secondJob = secondScope.ServiceProvider.GetRequiredService<ExpireStalePendingOrdersJob>();

        // The first run takes the lock and is held right after; the second runs meanwhile and must skip.
        var first = firstJob.RunAsync(Ct);
        await gate.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = await secondJob.RunAsync(Ct);
        gate.Release.TrySetResult();
        var expired = await first;

        second.ShouldBeNull();
        expired.ShouldBe(2);
        (await host.ScalarAsync<string>(StatusOf, ("id", stale1))).ShouldBe("Expired");
        (await host.ScalarAsync<string>(StatusOf, ("id", stale2))).ShouldBe("Expired");
        (await host.ScalarAsync<string>(StatusOf, ("id", fresh))).ShouldBe("Pending");
        (await host.ScalarAsync<string>(StatusOf, ("id", staleConfirmed))).ShouldBe("Confirmed");

        // The lock was released: a later run works (and finds nothing left to expire).
        using var laterScope = host.Factory.Services.CreateScope();
        (await laterScope.ServiceProvider.GetRequiredService<ExpireStalePendingOrdersJob>().RunAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_MoreThanOneBatch_ExpiresEveryStalePendingOrder()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.UserAsync("customer@example.com", "Customer");
        var template = await CreateOrderIdAsync(host, customer);
        const int Copies = ExpireStalePendingOrdersJob.BatchSize + 30;
        await host.ScalarAsync<int>(
            "INSERT INTO orders.orders (id, customer_id, status, currency, total, created_at, updated_at) "
            + "SELECT gen_random_uuid(), customer_id, 'Pending', currency, total, now() - interval '2 days', now() FROM orders.orders o, generate_series(1, @copies) WHERE o.id = @id",
            ("copies", Copies),
            ("id", template));

        using var scope = host.Factory.Services.CreateScope();
        var expired = await scope.ServiceProvider.GetRequiredService<ExpireStalePendingOrdersJob>().RunAsync(Ct);

        expired.ShouldBe(Copies);
        (await host.ScalarAsync<long>("SELECT count(*) FROM orders.orders WHERE status = 'Expired'")).ShouldBe(Copies);
        (await host.ScalarAsync<string>(StatusOf, ("id", template))).ShouldBe("Pending");
    }

    [Fact]
    public void Schedule_IsEveryFifteenMinutesUnderTheExpireStaleLock()
    {
        ExpireStalePendingOrdersJob.CronExpression.ShouldBe("*/15 * * * *");
        ExpireStalePendingOrdersJob.LockName.ShouldBe("orders:expire-stale");
        ExpireStalePendingOrdersJob.BatchSize.ShouldBe(100);
    }

    /// <summary>A host whose expiry runs are started only by the test, with the gated lock in front of the real one.</summary>
    private Task<OrdersTestHost> CreateHostAsync() => OrdersTestHost.CreateAsync(postgres, configureServices: services =>
    {
        // Keep the cron schedule from starting runs of its own during the test.
        foreach (var scheduler in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OrdersJobScheduler)).ToList())
        {
            services.Remove(scheduler);
        }

        var inner = services.Last(d => d.ServiceType == typeof(IDistributedLock) && !d.IsKeyedService);
        services.Remove(inner);
        services.AddSingleton(sp => new GatedLock(Resolve(sp, inner)));
        services.AddSingleton<IDistributedLock>(sp => sp.GetRequiredService<GatedLock>());
    });

    private static IDistributedLock Resolve(IServiceProvider services, ServiceDescriptor descriptor) =>
        (IDistributedLock)(descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(services)
            ?? ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!));

    private static async Task<Guid> CreateOrderIdAsync(OrdersTestHost host, TestUser customer) =>
        (await host.CreateOrderAsync(customer, ("SKU", 1, 1m))).GetProperty("id").GetGuid();

    private static Task<int> AgeAsync(OrdersTestHost host, TimeSpan age, params Guid[] ids) =>
        host.ScalarAsync<int>(
            "UPDATE orders.orders SET created_at = now() - @age WHERE id = ANY(@ids)",
            ("age", age),
            ("ids", ids));

    /// <summary>
    /// The real lock, except that once <see cref="Arm"/> is called, the next successful acquisition of the expiry lock
    /// signals <see cref="Acquired"/> and then waits for <see cref="Release"/> before returning, so a second run overlaps
    /// it deterministically.
    /// </summary>
    internal sealed class GatedLock(IDistributedLock inner) : IDistributedLock
    {
        private int _armed;

        public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var handle = await inner.TryAcquireAsync(name, timeout, cancellationToken);
            if (handle is not null
                && name == ExpireStalePendingOrdersJob.LockName
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Acquired.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return handle;
        }
    }
}
