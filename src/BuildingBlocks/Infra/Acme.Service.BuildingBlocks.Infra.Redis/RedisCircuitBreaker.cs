namespace Acme.Service.BuildingBlocks.Infra.Redis;

/// <summary>
/// Consecutive-failure circuit breaker in front of Redis, so an outage does not cost a timeout on every request.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Closed</b>: calls pass. <see cref="FailureThreshold"/> consecutive failures (default 5) open the circuit;
/// any success resets the count.</item>
/// <item><b>Open</b>: calls are rejected without I/O for <see cref="BreakDuration"/> (default 30 s).</item>
/// <item><b>Half-open</b>: after the break, exactly one trial call passes. Success closes the circuit, failure
/// reopens it for another <see cref="BreakDuration"/>; an abandoned trial (for example cancelled) lets the next
/// caller try.</item>
/// </list>
/// Thread-safe. Time comes from <see cref="TimeProvider"/>.
/// </remarks>
internal sealed class RedisCircuitBreaker
{
    /// <summary>Default number of consecutive failures that open the circuit.</summary>
    public const int DefaultFailureThreshold = 5;

    /// <summary>Default time the circuit stays open.</summary>
    public static readonly TimeSpan DefaultBreakDuration = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;
    private bool _trialInFlight;

    public RedisCircuitBreaker(TimeProvider timeProvider, int failureThreshold = DefaultFailureThreshold, TimeSpan? breakDuration = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(failureThreshold, 1);
        var duration = breakDuration ?? DefaultBreakDuration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero, nameof(breakDuration));

        _timeProvider = timeProvider;
        FailureThreshold = failureThreshold;
        BreakDuration = duration;
    }

    /// <summary>Consecutive failures that open the circuit.</summary>
    public int FailureThreshold { get; }

    /// <summary>How long the circuit stays open before a trial call is allowed.</summary>
    public TimeSpan BreakDuration { get; }

    /// <summary>The current state.</summary>
    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return CurrentState();
            }
        }
    }

    /// <summary>True while calls are rejected (open, or half-open with the trial call in flight).</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                var state = CurrentState();
                return state == CircuitState.Open || (state == CircuitState.HalfOpen && _trialInFlight);
            }
        }
    }

    /// <summary>
    /// Returns true when a call may proceed. The caller must then report the outcome with
    /// <see cref="RecordSuccess"/>, <see cref="RecordFailure"/> or <see cref="RecordAbandoned"/>.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            switch (CurrentState())
            {
                case CircuitState.Closed:
                    return true;
                case CircuitState.HalfOpen when !_trialInFlight:
                    _trialInFlight = true;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Reports a successful call: closes the circuit and resets the failure count.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
            _trialInFlight = false;
        }
    }

    /// <summary>Reports a failed call. Returns true when this failure opened (or reopened) the circuit.</summary>
    public bool RecordFailure()
    {
        lock (_gate)
        {
            var state = CurrentState();
            _trialInFlight = false;

            if (state == CircuitState.Open)
            {
                // A call that was admitted before the circuit opened; it neither extends nor counts.
                return false;
            }

            _consecutiveFailures++;
            if (state == CircuitState.HalfOpen || _consecutiveFailures >= FailureThreshold)
            {
                _openUntil = _timeProvider.GetUtcNow() + BreakDuration;
                return true;
            }

            return false;
        }
    }

    /// <summary>Reports an admitted call that ended without an outcome (for example cancelled by the caller).</summary>
    public void RecordAbandoned()
    {
        lock (_gate)
        {
            _trialInFlight = false;
        }
    }

    private CircuitState CurrentState()
    {
        if (_openUntil is not { } until)
        {
            return CircuitState.Closed;
        }

        return _timeProvider.GetUtcNow() < until ? CircuitState.Open : CircuitState.HalfOpen;
    }
}

/// <summary>State of <see cref="RedisCircuitBreaker"/>.</summary>
internal enum CircuitState
{
    /// <summary>Calls pass.</summary>
    Closed,

    /// <summary>Calls are rejected.</summary>
    Open,

    /// <summary>The break elapsed; one trial call may pass.</summary>
    HalfOpen,
}
