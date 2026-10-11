using Acme.Service.BuildingBlocks.Persistence.Locking;
using Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Messaging;

[Collection(PostgresTestGroup.Name)]
public sealed class PostgresDistributedLockTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TryAcquireAsync_HeldOnAnotherConnection_IsExclusiveUntilDisposed()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        var replicaA = Create(connectionString);
        var replicaB = Create(connectionString);

        var held = await replicaA.TryAcquireAsync("jobs.cleanup", TimeSpan.Zero, Ct);
        held.ShouldNotBeNull();

        (await replicaB.TryAcquireAsync("jobs.cleanup", TimeSpan.Zero, Ct)).ShouldBeNull();
        (await replicaB.TryAcquireAsync("jobs.cleanup", TimeSpan.FromMilliseconds(300), Ct)).ShouldBeNull();

        var other = await replicaB.TryAcquireAsync("jobs.other", TimeSpan.Zero, Ct);
        other.ShouldNotBeNull();
        await other.DisposeAsync();

        await held.DisposeAsync();
        await held.DisposeAsync();

        var reacquired = await replicaB.TryAcquireAsync("jobs.cleanup", TimeSpan.Zero, Ct);
        reacquired.ShouldNotBeNull();
        await reacquired.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TryAcquireAsync_ReleasedWhileWaiting_AcquiresBeforeTimeout()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        var replicaA = Create(connectionString);
        var replicaB = Create(connectionString);
        var held = (await replicaA.TryAcquireAsync("jobs.report", TimeSpan.Zero, Ct)).ShouldNotBeNull();

        var waiting = replicaB.TryAcquireAsync("jobs.report", TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(300, Ct);
        waiting.IsCompleted.ShouldBeFalse();
        await held.DisposeAsync();

        var acquired = (await waiting).ShouldNotBeNull();
        await acquired.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TryAcquireAsync_HolderConnectionLost_LockIsReleasedAndDisposeDoesNotThrow()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        var replicaA = Create(connectionString);
        var replicaB = Create(connectionString);
        var held = (await replicaA.TryAcquireAsync("jobs.sync", TimeSpan.Zero, Ct)).ShouldNotBeNull();

        await postgres.TerminateConnectionsAsync(connectionString, Ct);

        var acquired = (await replicaB.TryAcquireAsync("jobs.sync", TimeSpan.FromSeconds(5), Ct)).ShouldNotBeNull();
        await Should.NotThrowAsync(async () => await held.DisposeAsync());
        await acquired.DisposeAsync();
    }

    private static PostgresDistributedLock Create(string connectionString) =>
        new(
            Options.Create(new PostgresLockOptions { ConnectionString = connectionString }),
            TimeProvider.System,
            NullLogger<PostgresDistributedLock>.Instance);
}
