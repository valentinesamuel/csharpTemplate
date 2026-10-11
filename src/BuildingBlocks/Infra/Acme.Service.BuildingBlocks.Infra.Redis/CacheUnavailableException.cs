namespace Acme.Service.BuildingBlocks.Infra.Redis;

/// <summary>
/// Thrown by <see cref="RedisCache.TryAddAsync{T}"/> when Redis cannot be reached or the circuit breaker is open.
/// An atomic add cannot degrade to a silent "miss" without breaking its guarantee, so the caller decides (for
/// example the idempotency filter applies its FailMode). The message never contains the connection string (INV-28).
/// </summary>
public sealed class CacheUnavailableException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public CacheUnavailableException()
        : base("The cache is unavailable.")
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public CacheUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the underlying failure.</summary>
    public CacheUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
