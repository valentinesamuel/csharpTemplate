using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Locking;
using Acme.Service.BuildingBlocks.Persistence.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Messaging;

public sealed class OutboxCleanupJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Constants_HourlyRecurringJobAndOwnLock()
    {
        OutboxCleanupJob.RecurringJobId.ShouldBe("outbox-cleanup");
        OutboxCleanupJob.CronExpression.ShouldBe("0 * * * *");
        OutboxCleanupJob.LockName.ShouldBe("outbox-cleanup");
    }

    [Fact]
    public async Task ExecuteAsync_LockAcquired_DeletesOutboxThenInboxAndReleasesLock()
    {
        var retention = new FakeRetention();
        var handle = Substitute.For<IAsyncDisposable>();
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(OutboxCleanupJob.LockName, TimeSpan.Zero, Arg.Any<CancellationToken>()).Returns(handle);

        await Job(retention, distributedLock).ExecuteAsync(Ct);

        retention.Calls.ShouldBe(["outbox", "inbox"]);
        await handle.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_LockHeldElsewhere_SkipsWithoutDeleting()
    {
        var retention = new FakeRetention();
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns((IAsyncDisposable?)null);

        await Job(retention, distributedLock).ExecuteAsync(Ct);

        retention.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_CleanupFails_ThrowsAndReleasesLock()
    {
        var retention = new FakeRetention { Failure = new InvalidOperationException("database down") };
        var handle = Substitute.For<IAsyncDisposable>();
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(handle);

        await Should.ThrowAsync<InvalidOperationException>(() => Job(retention, distributedLock).ExecuteAsync(Ct));

        await handle.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_ResolvedLikeAJobProvider_UsesRegisteredRetentionAndLock()
    {
        var retention = new FakeRetention();
        var distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageRetention>(retention);
        services.AddSingleton(distributedLock);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var job = ActivatorUtilities.GetServiceOrCreateInstance<OutboxCleanupJob>(scope.ServiceProvider);
        await job.ExecuteAsync(Ct);

        retention.Calls.ShouldBe(["outbox", "inbox"]);
    }

    [Fact]
    public async Task Scheduler_JobQueueRegistered_RegistersRecurringJobOnce()
    {
        var queue = Substitute.For<IJobQueue>();
        using var scheduler = Scheduler(queue);

        await RunToCompletionAsync(scheduler);

        await queue.Received(1).AddOrUpdateRecurringAsync<OutboxCleanupJob>("outbox-cleanup", "0 * * * *", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Scheduler_RegistrationFails_RetriesUntilItSucceeds()
    {
        var queue = Substitute.For<IJobQueue>();
        queue.AddOrUpdateRecurringAsync<OutboxCleanupJob>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new InvalidOperationException("relation \"hangfire.hash\" does not exist"),
                _ => throw new InvalidOperationException("still missing"),
                _ => Task.CompletedTask);
        using var scheduler = Scheduler(queue);
        scheduler.RetryDelay = TimeSpan.FromMilliseconds(10);

        await RunToCompletionAsync(scheduler);

        await queue.Received(3).AddOrUpdateRecurringAsync<OutboxCleanupJob>("outbox-cleanup", "0 * * * *", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Scheduler_StartAsync_DoesNotWaitForTheJobQueue()
    {
        var gate = new TaskCompletionSource();
        var queue = Substitute.For<IJobQueue>();
        queue.AddOrUpdateRecurringAsync<OutboxCleanupJob>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                gate.Task.Wait(TimeSpan.FromSeconds(10), Ct);
                return Task.CompletedTask;
            });
        using var scheduler = Scheduler(queue);

        var start = scheduler.StartAsync(Ct);

        start.IsCompleted.ShouldBeTrue();
        gate.SetResult();
        await RunToCompletionAsync(scheduler, alreadyStarted: true);
    }

    [Fact]
    public async Task Scheduler_NoJobQueue_DoesNothing()
    {
        using var scheduler = new OutboxCleanupScheduler(
            new ServiceCollection().BuildServiceProvider(),
            TimeProvider.System,
            NullLogger<OutboxCleanupScheduler>.Instance);

        await RunToCompletionAsync(scheduler);

        scheduler.ExecuteTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task Scheduler_StoppedWhileRetrying_Stops()
    {
        var queue = Substitute.For<IJobQueue>();
        queue.AddOrUpdateRecurringAsync<OutboxCleanupJob>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("down"));
        using var scheduler = Scheduler(queue);
        scheduler.RetryDelay = TimeSpan.FromMinutes(10);

        await scheduler.StartAsync(Ct);
        await WaitUntilAsync(() => queue.ReceivedCalls().Any());
        await scheduler.StopAsync(Ct);

        scheduler.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeTrue();
    }

    private static OutboxCleanupJob Job(IMessageRetention retention, IDistributedLock distributedLock) =>
        new(retention, distributedLock, NullLogger<OutboxCleanupJob>.Instance);

    private static OutboxCleanupScheduler Scheduler(IJobQueue queue)
    {
        var services = new ServiceCollection();
        services.AddSingleton(queue);
        return new OutboxCleanupScheduler(services.BuildServiceProvider(), TimeProvider.System, NullLogger<OutboxCleanupScheduler>.Instance);
    }

    private static async Task RunToCompletionAsync(OutboxCleanupScheduler scheduler, bool alreadyStarted = false)
    {
        if (!alreadyStarted)
        {
            await scheduler.StartAsync(Ct);
        }

        await scheduler.ExecuteTask.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            await Task.Delay(10, Ct);
        }
    }

    private sealed class FakeRetention : IMessageRetention
    {
        public List<string> Calls { get; } = [];

        public Exception? Failure { get; init; }

        public Task<int> DeleteProcessedOutboxMessagesAsync(CancellationToken cancellationToken)
        {
            Calls.Add("outbox");
            return Failure is null ? Task.FromResult(2) : Task.FromException<int>(Failure);
        }

        public Task<int> DeleteExpiredInboxMessagesAsync(CancellationToken cancellationToken)
        {
            Calls.Add("inbox");
            return Task.FromResult(3);
        }
    }
}
