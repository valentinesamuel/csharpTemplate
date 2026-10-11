using Acme.Service.BuildingBlocks.Infra.InMemory;
using Acme.Service.BuildingBlocks.Locking;
using Acme.Service.Testing.Contracts;
using Shouldly;

namespace Acme.Service.Infra.Tests.InMemory;

public sealed class InMemoryDistributedLockContractTests : DistributedLockContractTests
{
    private readonly InMemoryDistributedLock _lock = new();

    protected override ValueTask<IDistributedLock> CreateLockAsync() => ValueTask.FromResult<IDistributedLock>(_lock);

    [Fact]
    public async Task TryAcquireAsync_AllHandlesReleased_TracksNoNames()
    {
        var name = NewName();
        var first = await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct);
        (await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct)).ShouldBeNull();
        first.ShouldNotBeNull();

        await first.DisposeAsync();

        _lock.TrackedNameCount.ShouldBe(0);
    }

    [Fact]
    public async Task TryAcquireAsync_ManyConcurrentCallers_OneHolderAtATime()
    {
        var name = NewName();
        var inside = 0;
        var maxInside = 0;

        var workers = Enumerable.Range(0, 16).Select(_ => Task.Run(
            async () =>
            {
                var handle = await Lock.TryAcquireAsync(name, TimeSpan.FromSeconds(30), Ct);
                handle.ShouldNotBeNull();
                await using (handle)
                {
                    var now = Interlocked.Increment(ref inside);
                    InterlockedMax(ref maxInside, now);
                    await Task.Delay(5, Ct);
                    Interlocked.Decrement(ref inside);
                }
            },
            Ct));

        await Task.WhenAll(workers);

        maxInside.ShouldBe(1);
        _lock.TrackedNameCount.ShouldBe(0);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
