using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Acme.Service.BuildingBlocks.Infra.Redis;

/// <summary>
/// Readiness check for the Redis cache (tag <c>ready</c>). It reports <see cref="HealthStatus.Degraded"/>, never
/// <see cref="HealthStatus.Unhealthy"/>, when Redis is unavailable (D-20): a cache outage must not take every replica
/// out of rotation. The PING goes through the circuit breaker, so an open circuit costs no I/O. Descriptions never
/// include the connection string (INV-28).
/// </summary>
internal sealed class RedisHealthCheck(RedisCache cache) : IHealthCheck
{
    /// <summary>Registration name of the check.</summary>
    public const string Name = "redis";

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (cache.Breaker.IsOpen)
        {
            return HealthCheckResult.Degraded("Redis is unavailable (circuit breaker open); cache reads miss and writes are skipped.");
        }

        return await cache.PingAsync(cancellationToken).ConfigureAwait(false)
            ? HealthCheckResult.Healthy("Redis is reachable.")
            : HealthCheckResult.Degraded("Redis is unavailable; cache reads miss and writes are skipped.");
    }
}
