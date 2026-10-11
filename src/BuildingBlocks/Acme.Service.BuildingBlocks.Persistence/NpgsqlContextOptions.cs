using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>Npgsql configuration shared by the DI registration and the design-time factories.</summary>
internal static class NpgsqlContextOptions
{
    /// <summary>EF migrations history table; one per module, inside the module schema (plan §0.5).</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>Retries of a retryable operation after the first failure (D-28).</summary>
    public const int MaxRetryCount = 3;

    /// <summary>Upper bound of the delay between retries; keeps request latency bounded.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Configures Npgsql with the module's migrations assembly and history table. EnableRetryOnFailure is
    /// deliberately NOT set (D-28): retries are applied per operation by <see cref="CreateRetryingStrategy"/>.
    /// </summary>
    public static void Configure(DbContextOptionsBuilder options, string connectionString, string schema, Assembly migrationsAssembly) =>
        options.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsAssembly(migrationsAssembly.GetName().Name)
            .MigrationsHistoryTable(MigrationsHistoryTable, schema));

    /// <summary>A retrying execution strategy for queries and retryable commands only (D-28, INV-26).</summary>
    public static IExecutionStrategy CreateRetryingStrategy(DbContext context) => new TransientRetryStrategy(context);

    /// <summary>
    /// Npgsql retrying strategy that also recognises a transient fault wrapped by another exception. The
    /// context's default (non-retrying) strategy wraps transient faults in an <see cref="InvalidOperationException"/>,
    /// which the base strategy does not unwrap, so it would never retry (WH-27).
    /// </summary>
    private sealed class TransientRetryStrategy(DbContext context)
        : NpgsqlRetryingExecutionStrategy(context, NpgsqlContextOptions.MaxRetryCount, NpgsqlContextOptions.MaxRetryDelay, errorCodesToAdd: null)
    {
        protected override bool ShouldRetryOn(Exception? exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (base.ShouldRetryOn(current))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
