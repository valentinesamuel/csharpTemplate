using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Acme.Service.BuildingBlocks.Persistence.Health;

/// <summary>
/// Readiness check (D-20): unhealthy while any module context has pending migrations, so an app started
/// before <c>migrate</c> stays out of rotation; the web host never migrates itself (INV-16). Results are cached
/// for <see cref="CacheDuration"/>; a failure to read the migration history is not cached.
/// </summary>
public sealed class PendingMigrationsHealthCheck(
    IEnumerable<MigrationTarget> targets,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IHealthCheck, IDisposable
{
    /// <summary>How long a result is reused.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<MigrationTarget> _targets = [.. targets];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedResult? _cached;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (TryGetCached(out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(out cached))
            {
                return cached;
            }

            var result = await EvaluateAsync(context.Registration.FailureStatus, cancellationToken).ConfigureAwait(false);
            if (result.Exception is null)
            {
                Volatile.Write(ref _cached, new CachedResult(result, timeProvider.GetUtcNow() + CacheDuration));
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private bool TryGetCached(out HealthCheckResult result)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && timeProvider.GetUtcNow() < cached.ExpiresAt)
        {
            result = cached.Result;
            return true;
        }

        result = default;
        return false;
    }

    private async Task<HealthCheckResult> EvaluateAsync(HealthStatus failureStatus, CancellationToken cancellationToken)
    {
        if (_targets.Count == 0)
        {
            return HealthCheckResult.Healthy("No module contexts registered.");
        }

        var pending = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            foreach (var target in _targets)
            {
                var scope = scopeFactory.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(target.ContextType);
                    var migrations = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                    if (migrations.Count > 0)
                    {
                        pending[target.ModuleKey] = migrations.Count;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(failureStatus, "Could not read the migration history.", ex);
        }

        return pending.Count == 0
            ? HealthCheckResult.Healthy("No pending migrations.")
            : new HealthCheckResult(
                failureStatus,
                $"Pending migrations in: {string.Join(", ", pending.Keys)}. Run the migrate command.",
                data: pending);
    }

    private sealed record CachedResult(HealthCheckResult Result, DateTimeOffset ExpiresAt);
}
