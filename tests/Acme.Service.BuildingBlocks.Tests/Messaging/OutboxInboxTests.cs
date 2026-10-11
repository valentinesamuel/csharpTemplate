using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Persistence.Messaging;
using Acme.Service.BuildingBlocks.Persistence.Outbox;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.Tests.Messaging.Fixtures;
using Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Messaging;

[Collection(PostgresTestGroup.Name)]
public sealed class OutboxInboxTests(PostgresFixture postgres)
{
    private const string Gear = "gear";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enqueue_SuccessfulUnitOfWork_WritesOutboxRowWithBusinessRow()
    {
        await using var host = await MessagingHost.CreateAsync(postgres, Ct);
        var integrationEvent = WidgetOrderedV1.Create(Gear);

        var result = await host.InTransactionAsync(
            async (context, outbox, ct) =>
            {
                await context.Widgets.AddAsync(new Widget { Name = Gear }, ct);
                outbox.Enqueue(integrationEvent);
                return Result.Success();
            },
            Ct);

        result.IsSuccess.ShouldBeTrue();
        (await host.ReadAllAsync(Ct)).ShouldHaveSingleItem().Name.ShouldBe(Gear);
        var row = (await host.OutboxAsync(Ct)).ShouldHaveSingleItem();
        row.Id.ShouldBe(integrationEvent.Id);
        row.Type.ShouldBe("widgets.widget-ordered.v1");
        row.Attempts.ShouldBe(0);
        row.ProcessedAt.ShouldBeNull();
        row.DeadAt.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enqueue_FailedUnitOfWork_WritesNeitherOutboxRowNorBusinessRow()
    {
        await using var host = await MessagingHost.CreateAsync(postgres, Ct);

        var result = await host.InTransactionAsync(
            async (context, outbox, ct) =>
            {
                await context.Widgets.AddAsync(new Widget { Name = Gear }, ct);
                outbox.Enqueue(WidgetOrderedV1.Create(Gear));
                await context.SaveChangesAsync(ct);
                return Error.Unprocessable("rule_broken", "No.");
            },
            Ct);

        result.IsFailure.ShouldBeTrue();
        (await host.ReadAllAsync(Ct)).ShouldBeEmpty();
        (await host.OutboxAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enqueue_UnregisteredEventType_ThrowsInsteadOfWriting()
    {
        await using var host = await MessagingHost.CreateAsync(postgres, Ct);
        await using var scope = host.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutbox>(WidgetsDbContext.ModuleKey);

        Should.Throw<InvalidOperationException>(() => outbox.Enqueue(new UnregisteredEvent(Guid.CreateVersion7(), TimeProvider.System.GetUtcNow())))
            .Message.ShouldContain("not registered");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DispatcherHostedService_EnqueuedEvent_DeliveredThroughInboxWithCorrelationId()
    {
        await using var host = await MessagingHost.CreateAsync(postgres, Ct, services =>
        {
            MessagingHost.SubscribeIncrement(services);
            services.Configure<OutboxOptions>(o => o.PollingInterval = TimeSpan.FromMilliseconds(50));
        });
        await host.SeedAsync(Gear, Ct);
        var correlation = host.Services.GetRequiredService<ICorrelationContext>();
        correlation.CorrelationId = "corr-123";
        var integrationEvent = WidgetOrderedV1.Create(Gear, amount: 5);
        await host.EnqueueAsync(Ct, integrationEvent);
        correlation.CorrelationId = null;

        (await host.OutboxAsync(Ct)).ShouldHaveSingleItem().CorrelationId.ShouldBe("corr-123");

        var dispatcher = host.Dispatcher();
        await dispatcher.StartAsync(Ct);
        try
        {
            await WaitUntilAsync(async () => (await host.OutboxAsync(Ct)).Single().ProcessedAt is not null);
        }
        finally
        {
            await dispatcher.StopAsync(Ct);
        }

        (await host.QuantityAsync(Gear, Ct)).ShouldBe(1 + 5);
        var inbox = (await host.InboxAsync(Ct)).ShouldHaveSingleItem();
        inbox.MessageId.ShouldBe(integrationEvent.Id);
        inbox.Consumer.ShouldBe("widgets.IncrementWidgetHandler");
        host.Probe().CorrelationIds.ShouldBe(["corr-123"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DispatchBatchAsync_TwoDispatchersInParallel_DeliverEachMessageExactlyOnce()
    {
        var deliveries = new ConcurrentDictionary<Guid, int>();
        Action<IServiceCollection> replica = services =>
        {
            services.AddSingleton<IEventBus>(new CountingEventBus(deliveries));
            services.Configure<OutboxOptions>(o => o.BatchSize = 5);
        };
        await using var first = await MessagingHost.CreateAsync(postgres, Ct, replica);
        await using var second = new ReplicaHost(WidgetsHost.BuildServices(first.ConnectionString, configure: services =>
        {
            services.AddIntegrationEvents(typeof(WidgetOrderedV1).Assembly);
            replica(services);
        }));

        var events = Enumerable.Range(0, 60).Select(i => (IIntegrationEvent)WidgetOrderedV1.Create($"w{i}")).ToArray();
        await first.EnqueueAsync(Ct, events);

        await Task.WhenAll(
            DrainAsync(first.Dispatcher()),
            DrainAsync(second.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<OutboxDispatcher<WidgetsDbContext>>().Single()));

        deliveries.Count.ShouldBe(60);
        deliveries.Values.ShouldAllBe(count => count == 1);
        (await first.OutboxAsync(Ct)).ShouldAllBe(m => m.ProcessedAt != null && m.Attempts == 0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DispatchBatchAsync_CrashAfterHandlerCommitBeforeMarkProcessed_RedeliveryIsDedupedByInbox()
    {
        var crash = new CrashBeforeMarkProcessedInterceptor { CrashesRemaining = 1 };
        await using var host = await MessagingHost.CreateAsync(postgres, Ct, services =>
        {
            MessagingHost.SubscribeIncrement(services);
            services.ConfigureDbContext<WidgetsDbContext>(options => options.AddInterceptors(crash));
        });
        await host.SeedAsync(Gear, Ct);
        await host.EnqueueAsync(Ct, WidgetOrderedV1.Create(Gear, amount: 10));

        await Should.ThrowAsync<InvalidOperationException>(() => host.Dispatcher().DispatchBatchAsync(Ct));

        // The consumer committed, the outbox row did not: it is still pending and will be redelivered.
        (await host.QuantityAsync(Gear, Ct)).ShouldBe(11);
        (await host.OutboxAsync(Ct)).Single().ProcessedAt.ShouldBeNull();

        (await host.Dispatcher().DispatchBatchAsync(Ct)).ShouldBe(1);

        (await host.QuantityAsync(Gear, Ct)).ShouldBe(11);
        host.Probe().Calls(IncrementWidgetHandler.Name).ShouldBe(1);
        (await host.OutboxAsync(Ct)).Single().ProcessedAt.ShouldNotBeNull();
        (await host.InboxAsync(Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DispatchBatchAsync_HandlerKeepsFailing_BacksOffThenDeadAfterMaxAttempts()
    {
        var clock = new ManualTimeProvider(TimeProvider.System.GetUtcNow());
        await using var host = await MessagingHost.CreateAsync(postgres, Ct, services =>
        {
            MessagingHost.SubscribeIncrement(services);
            services.AddSingleton<TimeProvider>(clock);
            services.Configure<OutboxOptions>(o => o.MaxAttempts = 3);
        });
        await host.SeedAsync(Gear, Ct);
        await host.EnqueueAsync(Ct, WidgetOrderedV1.Create(Gear));
        host.Probe().FailNext(IncrementWidgetHandler.Name, int.MaxValue);
        using var dead = ListenForDead(host);
        var dispatcher = host.Dispatcher();

        (await dispatcher.DispatchBatchAsync(Ct)).ShouldBe(1);
        var afterFirst = (await host.OutboxAsync(Ct)).Single();
        afterFirst.Attempts.ShouldBe(1);
        afterFirst.NextAttemptAt.ShouldBe(clock.GetUtcNow() + TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));
        afterFirst.LastError.ShouldNotBeNull().ShouldContain("Simulated failure");

        // Not due yet: the backoff holds the message back.
        (await dispatcher.DispatchBatchAsync(Ct)).ShouldBe(0);

        clock.Advance(TimeSpan.FromSeconds(2));
        (await dispatcher.DispatchBatchAsync(Ct)).ShouldBe(1);
        var afterSecond = (await host.OutboxAsync(Ct)).Single();
        afterSecond.Attempts.ShouldBe(2);
        afterSecond.NextAttemptAt.ShouldBe(clock.GetUtcNow() + TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(1));

        clock.Advance(TimeSpan.FromSeconds(4));
        (await dispatcher.DispatchBatchAsync(Ct)).ShouldBe(1);
        var final = (await host.OutboxAsync(Ct)).Single();
        final.Attempts.ShouldBe(3);
        final.DeadAt.ShouldNotBeNull();
        final.ProcessedAt.ShouldBeNull();

        clock.Advance(TimeSpan.FromHours(1));
        (await dispatcher.DispatchBatchAsync(Ct)).ShouldBe(0);
        host.Probe().Calls(IncrementWidgetHandler.Name).ShouldBe(3);
        (await host.QuantityAsync(Gear, Ct)).ShouldBe(1);
        (await host.InboxAsync(Ct)).ShouldBeEmpty();
        dead.Total.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DispatchBatchAsync_OneOfTwoSubscribersFails_SucceededSubscriberIsNotReapplied()
    {
        var clock = new ManualTimeProvider(TimeProvider.System.GetUtcNow());
        await using var host = await MessagingHost.CreateAsync(postgres, Ct, services =>
        {
            MessagingHost.SubscribeIncrement(services);
            MessagingHost.SubscribeAudit(services);
            services.AddSingleton<TimeProvider>(clock);
        });
        await host.SeedAsync(Gear, Ct);
        var integrationEvent = WidgetOrderedV1.Create(Gear, amount: 3);
        await host.EnqueueAsync(Ct, integrationEvent);
        host.Probe().FailNext(AuditWidgetHandler.Name, 1);

        await host.Dispatcher().DispatchBatchAsync(Ct);

        (await host.OutboxAsync(Ct)).Single().Attempts.ShouldBe(1);
        (await host.InboxAsync(Ct)).ShouldHaveSingleItem().Consumer.ShouldBe("widgets.IncrementWidgetHandler");

        clock.Advance(TimeSpan.FromSeconds(2));
        await host.Dispatcher().DispatchBatchAsync(Ct);

        (await host.OutboxAsync(Ct)).Single().ProcessedAt.ShouldNotBeNull();
        (await host.QuantityAsync(Gear, Ct)).ShouldBe(1 + 3);
        host.Probe().Calls(IncrementWidgetHandler.Name).ShouldBe(1);
        host.Probe().Calls(AuditWidgetHandler.Name).ShouldBe(2);
        (await host.ReadAllAsync(Ct)).Count(w => w.Name == $"audit-{integrationEvent.Id:N}").ShouldBe(1);
        (await host.InboxAsync(Ct)).Select(i => i.Consumer).ShouldBe(["widgets.AuditWidgetHandler", "widgets.IncrementWidgetHandler"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DispatchBatchAsync_UnknownTypeAndUnreadablePayload_DeadWithErrorWithoutBlockingBatch()
    {
        await using var host = await MessagingHost.CreateAsync(postgres, Ct, MessagingHost.SubscribeIncrement);
        await host.SeedAsync(Gear, Ct);
        var start = TimeProvider.System.GetUtcNow();
        var before = WidgetOrderedV1.Create(Gear) with { Id = Guid.CreateVersion7(start) };
        var unknownId = Guid.CreateVersion7(start.AddMilliseconds(1));
        var unreadableId = Guid.CreateVersion7(start.AddMilliseconds(2));
        var after = WidgetOrderedV1.Create(Gear) with { Id = Guid.CreateVersion7(start.AddMilliseconds(3)) };
        await host.EnqueueAsync(Ct, before, after);
        await InsertRawAsync(host, unknownId, "widgets.no-such-event.v1", """{"id":"x"}""");
        await InsertRawAsync(host, unreadableId, "widgets.widget-ordered.v1", """{"amount":"not-a-number"}""");
        using var dead = ListenForDead(host);

        (await host.Dispatcher().DispatchBatchAsync(Ct)).ShouldBe(4);

        var rows = (await host.OutboxAsync(Ct)).ToDictionary(m => m.Id);
        rows[before.Id].ProcessedAt.ShouldNotBeNull();
        rows[after.Id].ProcessedAt.ShouldNotBeNull();
        rows[unknownId].DeadAt.ShouldNotBeNull();
        rows[unknownId].LastError.ShouldNotBeNull().ShouldContain("Unknown integration event type");
        rows[unreadableId].DeadAt.ShouldNotBeNull();
        var unreadableError = rows[unreadableId].LastError.ShouldNotBeNull();
        unreadableError.ShouldContain("could not be deserialized");
        unreadableError.ShouldNotContain("not-a-number");
        (await host.QuantityAsync(Gear, Ct)).ShouldBe(3);
        dead.Total.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Cleanup_ProcessedAndInboxRowsPastRetention_AreDeletedAndOthersKept()
    {
        var clock = new ManualTimeProvider(TimeProvider.System.GetUtcNow());
        await using var host = await MessagingHost.CreateAsync(postgres, Ct, services =>
        {
            MessagingHost.SubscribeIncrement(services);
            services.AddSingleton<TimeProvider>(clock);
        });
        await host.SeedAsync(Gear, Ct);
        await host.EnqueueAsync(Ct, WidgetOrderedV1.Create(Gear));
        await host.Dispatcher().DispatchBatchAsync(Ct);
        await host.EnqueueAsync(Ct, WidgetOrderedV1.Create(Gear));
        var cleanup = host.Services.GetRequiredService<OutboxCleanup>();

        clock.Advance(TimeSpan.FromDays(8));
        (await cleanup.DeleteProcessedOutboxMessagesAsync(Ct)).ShouldBe(1);
        (await cleanup.DeleteExpiredInboxMessagesAsync(Ct)).ShouldBe(0);
        (await host.OutboxAsync(Ct)).ShouldHaveSingleItem().ProcessedAt.ShouldBeNull();

        clock.Advance(TimeSpan.FromDays(30));
        (await cleanup.DeleteExpiredInboxMessagesAsync(Ct)).ShouldBe(1);
        (await host.InboxAsync(Ct)).ShouldBeEmpty();
    }

    private static async Task DrainAsync(OutboxDispatcher<WidgetsDbContext> dispatcher)
    {
        while (await dispatcher.DispatchBatchAsync(Ct) > 0)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(15);
        while (!await condition())
        {
            TimeProvider.System.GetUtcNow().ShouldBeLessThan(deadline, "Timed out waiting for the dispatcher.");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task InsertRawAsync(WidgetsHost host, Guid id, string type, string payload)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();
        var now = TimeProvider.System.GetUtcNow();
        context.Set<OutboxMessage>().Add(new OutboxMessage { Id = id, Type = type, Payload = payload, OccurredAt = now, NextAttemptAt = now.AddMinutes(-1) });
        await context.SaveChangesAsync(Ct);
    }

    private static DeadCounter ListenForDead(WidgetsHost host) =>
        new(host.Services.GetRequiredService<OutboxMetrics>().Meter);

    private sealed record UnregisteredEvent(Guid Id, DateTimeOffset OccurredAt) : IIntegrationEvent;

    /// <summary>Another app instance on the same database (a second replica).</summary>
    private sealed class ReplicaHost(ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    /// <summary>Sums <c>outbox.dead</c> measurements of one meter instance.</summary>
    private sealed class DeadCounter : IDisposable
    {
        private long _total;

        public DeadCounter(Meter meter)
        {
            var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == MessagingTelemetry.DeadCounterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _total, value));
            listener.Start();
            Listener = listener;
        }

        public MeterListener Listener { get; }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => Listener.Dispose();
    }
}
