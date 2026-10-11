using Acme.Service.BuildingBlocks.Persistence.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Persistence.Migrations;

/// <summary>
/// Applies EF migrations for every registered module context, in registration order, then runs the
/// <see cref="IPostMigrationStep"/>s. Used only by the <c>migrate</c> command (INV-16, D-07); register with
/// <see cref="PersistenceExtensions.AddMigrationRunner"/>, which also switches contexts to the
/// <c>PostgresMigrations</c> connection string.
/// </summary>
public sealed partial class MigrationRunner(
    IEnumerable<MigrationTarget> targets,
    IServiceScopeFactory scopeFactory,
    ILogger<MigrationRunner> logger)
{
    private readonly IReadOnlyList<MigrationTarget> _targets = [.. targets];

    /// <summary>Registered module contexts in migration order.</summary>
    public IReadOnlyList<MigrationTarget> Targets => _targets;

    /// <summary>Returns the pending migrations of every module context, in migration order.</summary>
    public async Task<IReadOnlyList<PendingMigrations>> ListPendingAsync(CancellationToken cancellationToken)
    {
        var pending = new List<PendingMigrations>(_targets.Count);
        foreach (var target in _targets)
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var context = ResolveContext(scope.ServiceProvider, target);
                var migrations = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
                pending.Add(new PendingMigrations(target.ModuleKey, target.ContextType.Name, [.. migrations]));
            }
        }

        return pending;
    }

    /// <summary>Name of the Postgres advisory lock that serializes concurrent <c>migrate</c> runs.</summary>
    public const string MigrationLockName = "migrate";

    /// <summary>
    /// Migrates every module context, then runs the post-migration steps. Idempotent: an up-to-date
    /// database is left unchanged. Throws on the first failure. Concurrent runs (several migrate jobs) are
    /// serialized by the Postgres advisory lock <see cref="MigrationLockName"/>, held for the whole run.
    /// </summary>
    /// <remarks>
    /// WH-08 fallback: EF Core's own migration lock does not serialize runs once a context has more than one
    /// migration (a second runner re-applied a later migration and failed with 42P07), so the runner takes
    /// an advisory lock itself. This also makes the post-migration steps run one runner at a time.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var migrationLock = await AcquireMigrationLockAsync(cancellationToken).ConfigureAwait(false);
        await using (migrationLock.ConfigureAwait(false))
        {
            await MigrateAndRunStepsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IAsyncDisposable> AcquireMigrationLockAsync(CancellationToken cancellationToken)
    {
        if (_targets.Count == 0)
        {
            return NoLock.Instance;
        }

        string? connectionString;
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            connectionString = ResolveContext(scope.ServiceProvider, _targets[0]).Database.GetConnectionString();
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("The migration connection string is not configured.");
        }

        LogWaitingForLock(logger);
        return await PostgresDistributedLock.AcquireAsync(connectionString, MigrationLockName, logger, cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateAndRunStepsAsync(CancellationToken cancellationToken)
    {
        foreach (var target in _targets)
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var context = ResolveContext(scope.ServiceProvider, target);
                var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                LogMigrating(logger, target.ModuleKey, pending.Count);

                // Serialized by the runner's advisory lock (see RunAsync); EF's own lock alone is not enough (WH-08).
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                LogMigrated(logger, target.ModuleKey);
            }
        }

        var stepScope = scopeFactory.CreateAsyncScope();
        await using (stepScope.ConfigureAwait(false))
        {
            var steps = stepScope.ServiceProvider.GetServices<IPostMigrationStep>()
                .Select((step, index) => (step, index))
                .OrderBy(s => s.step.Order)
                .ThenBy(s => s.index)
                .Select(s => s.step);

            foreach (var step in steps)
            {
                LogRunningStep(logger, step.Name);
                await step.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static ModuleDbContext ResolveContext(IServiceProvider services, MigrationTarget target)
    {
        var context = (ModuleDbContext)services.GetRequiredService(target.ContextType);

        // INV-06: the context maps only to the module's own schema, and its history table lives there too.
        if (!string.Equals(context.Schema, target.ModuleKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{target.ContextType.Name}.Schema is '{context.Schema}' but it is registered for module '{target.ModuleKey}'; they must be equal.");
        }

        return context;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrating module {Module}: {PendingCount} pending migration(s)")]
    private static partial void LogMigrating(ILogger logger, string module, int pendingCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Module {Module} is up to date")]
    private static partial void LogMigrated(ILogger logger, string module);

    [LoggerMessage(Level = LogLevel.Information, Message = "Running post-migration step {Step}")]
    private static partial void LogRunningStep(ILogger logger, string step);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for the migration lock")]
    private static partial void LogWaitingForLock(ILogger logger);

    private sealed class NoLock : IAsyncDisposable
    {
        public static readonly NoLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
