namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>
/// Minimal fake clock (Microsoft.Extensions.TimeProvider.Testing is not in the package ledger). Only
/// <see cref="GetUtcNow"/> is controlled; timers keep using the system clock.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _now += delta;
        }
    }
}
