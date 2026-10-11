using Acme.Service.BuildingBlocks.Locking;
using Shouldly;
using Xunit;

namespace Acme.Service.Testing.Contracts;

/// <summary>
/// Behaviour every <see cref="IDistributedLock"/> provider must have within one instance (P7; reusable by any
/// further lock provider). Cross-process exclusion is provider-specific and tested by the provider's own suite.
/// Lock names are unique per test.
/// </summary>
public abstract class DistributedLockContractTests : IAsyncLifetime
{
    private IDistributedLock? _lock;

    /// <summary>The lock under test (created per test).</summary>
    protected IDistributedLock Lock => _lock ?? throw new InvalidOperationException("The lock is not initialized.");

    /// <summary>Creates the lock under test.</summary>
    protected abstract ValueTask<IDistributedLock> CreateLockAsync();

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync() => _lock = await CreateLockAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        switch (_lock)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TryAcquireAsync_FreeLock_ReturnsHandle()
    {
        var handle = await Lock.TryAcquireAsync(NewName(), TimeSpan.Zero, Ct);

        handle.ShouldNotBeNull();
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_HeldLockZeroTimeout_ReturnsNull()
    {
        var name = NewName();
        var first = await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct);
        first.ShouldNotBeNull();

        try
        {
            (await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct)).ShouldBeNull();
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryAcquireAsync_HeldLockShortTimeout_ReturnsNullAfterWaiting()
    {
        var name = NewName();
        var first = await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct);
        first.ShouldNotBeNull();

        try
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            (await Lock.TryAcquireAsync(name, TimeSpan.FromMilliseconds(300), Ct)).ShouldBeNull();
            waited.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryAcquireAsync_AfterRelease_Succeeds()
    {
        var name = NewName();
        var first = await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct);
        first.ShouldNotBeNull();
        await first.DisposeAsync();

        var second = await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct);

        second.ShouldNotBeNull();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_ReleasedWhileWaiting_AcquiresWithinTimeout()
    {
        var name = NewName();
        var first = await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct);
        first.ShouldNotBeNull();

        var waiter = Lock.TryAcquireAsync(name, TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(200, Ct);
        await first.DisposeAsync();

        var second = await waiter;
        second.ShouldNotBeNull();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_DifferentNames_AreIndependent()
    {
        var first = await Lock.TryAcquireAsync(NewName(), TimeSpan.Zero, Ct);
        var second = await Lock.TryAcquireAsync(NewName(), TimeSpan.Zero, Ct);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Twice_DoesNotReleaseLaterHolder()
    {
        var name = NewName();
        var first = await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct);
        first.ShouldNotBeNull();
        await first.DisposeAsync();
        var second = await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct);
        second.ShouldNotBeNull();

        try
        {
            await first.DisposeAsync();

            (await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct)).ShouldBeNull();
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryAcquireAsync_CancelledWhileWaiting_ThrowsOperationCanceled()
    {
        var name = NewName();
        var first = await Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct);
        first.ShouldNotBeNull();

        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

            var exception = await Should.ThrowAsync<Exception>(() => Lock.TryAcquireAsync(name, TimeSpan.FromSeconds(30), cancellation.Token));

            exception.ShouldBeAssignableTo<OperationCanceledException>();
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task TryAcquireAsync_BlankName_ThrowsArgumentException(string name)
    {
        var exception = await Should.ThrowAsync<Exception>(() => Lock.TryAcquireAsync(name, TimeSpan.Zero, Ct));

        exception.ShouldBeAssignableTo<ArgumentException>();
    }

    [Fact]
    public async Task TryAcquireAsync_NegativeTimeout_ThrowsArgumentOutOfRange() =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Lock.TryAcquireAsync(NewName(), TimeSpan.FromSeconds(-1), Ct));

    /// <summary>The current test's cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A lock name unique to this call.</summary>
    protected static string NewName() => "contract-lock-" + Guid.NewGuid().ToString("N");
}
