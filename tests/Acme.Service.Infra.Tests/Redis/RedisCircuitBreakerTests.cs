using Acme.Service.BuildingBlocks.Infra.Redis;
using Acme.Service.Infra.Tests.Fixtures;
using Shouldly;

namespace Acme.Service.Infra.Tests.Redis;

public sealed class RedisCircuitBreakerTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void Defaults_AreFiveFailuresAndThirtySeconds()
    {
        var breaker = new RedisCircuitBreaker(_time);

        breaker.FailureThreshold.ShouldBe(5);
        breaker.BreakDuration.ShouldBe(TimeSpan.FromSeconds(30));
        breaker.State.ShouldBe(CircuitState.Closed);
        breaker.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void RecordFailure_FourConsecutive_StaysClosed()
    {
        var breaker = new RedisCircuitBreaker(_time);

        Fail(breaker, 4).ShouldBeFalse();

        breaker.State.ShouldBe(CircuitState.Closed);
        breaker.TryAcquire().ShouldBeTrue();
    }

    [Fact]
    public void RecordFailure_FiveConsecutive_OpensAndRejects()
    {
        var breaker = new RedisCircuitBreaker(_time);

        Fail(breaker, 5).ShouldBeTrue();

        breaker.State.ShouldBe(CircuitState.Open);
        breaker.IsOpen.ShouldBeTrue();
        breaker.TryAcquire().ShouldBeFalse();
    }

    [Fact]
    public void RecordSuccess_BetweenFailures_ResetsCount()
    {
        var breaker = new RedisCircuitBreaker(_time);
        Fail(breaker, 4);
        breaker.TryAcquire().ShouldBeTrue();
        breaker.RecordSuccess();

        Fail(breaker, 4).ShouldBeFalse();

        breaker.State.ShouldBe(CircuitState.Closed);
    }

    [Fact]
    public void Open_BeforeBreakElapses_KeepsRejecting()
    {
        var breaker = Opened();

        _time.Advance(TimeSpan.FromSeconds(29.9));

        breaker.State.ShouldBe(CircuitState.Open);
        breaker.TryAcquire().ShouldBeFalse();
    }

    [Fact]
    public void Open_AfterBreak_AllowsExactlyOneTrial()
    {
        var breaker = Opened();

        _time.Advance(TimeSpan.FromSeconds(30));

        breaker.State.ShouldBe(CircuitState.HalfOpen);
        breaker.IsOpen.ShouldBeFalse();
        breaker.TryAcquire().ShouldBeTrue();
        breaker.IsOpen.ShouldBeTrue();
        breaker.TryAcquire().ShouldBeFalse();
    }

    [Fact]
    public void HalfOpen_TrialSucceeds_Closes()
    {
        var breaker = Opened();
        _time.Advance(TimeSpan.FromSeconds(30));
        breaker.TryAcquire().ShouldBeTrue();

        breaker.RecordSuccess();

        breaker.State.ShouldBe(CircuitState.Closed);
        breaker.TryAcquire().ShouldBeTrue();
        breaker.TryAcquire().ShouldBeTrue();
    }

    [Fact]
    public void HalfOpen_TrialFails_ReopensForAnotherBreak()
    {
        var breaker = Opened();
        _time.Advance(TimeSpan.FromSeconds(30));
        breaker.TryAcquire().ShouldBeTrue();

        breaker.RecordFailure().ShouldBeTrue();

        breaker.State.ShouldBe(CircuitState.Open);
        _time.Advance(TimeSpan.FromSeconds(29));
        breaker.TryAcquire().ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(1));
        breaker.TryAcquire().ShouldBeTrue();
    }

    [Fact]
    public void HalfOpen_TrialAbandoned_LetsNextCallerTry()
    {
        var breaker = Opened();
        _time.Advance(TimeSpan.FromSeconds(30));
        breaker.TryAcquire().ShouldBeTrue();

        breaker.RecordAbandoned();

        breaker.State.ShouldBe(CircuitState.HalfOpen);
        breaker.TryAcquire().ShouldBeTrue();
    }

    [Fact]
    public void RecordFailure_WhileOpen_DoesNotExtendBreak()
    {
        var breaker = Opened();
        _time.Advance(TimeSpan.FromSeconds(20));

        breaker.RecordFailure().ShouldBeFalse();

        _time.Advance(TimeSpan.FromSeconds(10));
        breaker.State.ShouldBe(CircuitState.HalfOpen);
    }

    [Fact]
    public void Constructor_CustomSettings_AreHonoured()
    {
        var breaker = new RedisCircuitBreaker(_time, failureThreshold: 2, breakDuration: TimeSpan.FromSeconds(5));

        Fail(breaker, 2).ShouldBeTrue();
        _time.Advance(TimeSpan.FromSeconds(5));

        breaker.State.ShouldBe(CircuitState.HalfOpen);
    }

    [Fact]
    public void Constructor_InvalidSettings_Throw()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RedisCircuitBreaker(_time, failureThreshold: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new RedisCircuitBreaker(_time, breakDuration: TimeSpan.Zero));
    }

    [Fact]
    public async Task TryAcquire_ConcurrentCallersInHalfOpen_ExactlyOneAdmitted()
    {
        var breaker = Opened();
        _time.Advance(TimeSpan.FromSeconds(30));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ct = TestContext.Current.CancellationToken;

        var attempts = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(
                async () =>
                {
                    await start.Task;
                    return breaker.TryAcquire();
                },
                ct))
            .ToList();
        start.SetResult();

        (await Task.WhenAll(attempts)).Count(admitted => admitted).ShouldBe(1);
    }

    private RedisCircuitBreaker Opened()
    {
        var breaker = new RedisCircuitBreaker(_time);
        Fail(breaker, 5).ShouldBeTrue();
        return breaker;
    }

    private static bool Fail(RedisCircuitBreaker breaker, int count)
    {
        var opened = false;
        for (var i = 0; i < count; i++)
        {
            breaker.TryAcquire();
            opened = breaker.RecordFailure();
        }

        return opened;
    }
}
