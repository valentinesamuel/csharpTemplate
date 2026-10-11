using System.Diagnostics.CodeAnalysis;
using Acme.Service.BuildingBlocks.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>Maps expected database failures to <see cref="Error"/> values. Messages never contain database details.</summary>
internal static class DatabaseErrors
{
    /// <summary>Postgres SQLSTATE for a unique constraint violation.</summary>
    public const string UniqueViolationSqlState = "23505";

    public static readonly Error ConcurrencyConflict = Error.Conflict(
        "concurrency_conflict", "The resource was modified by another request. Reload it and try again.");

    public static readonly Error UniqueViolation = Error.Conflict(
        "unique_violation", "A resource with the same unique value already exists.");

    public static readonly Error Unavailable = Error.Unavailable(
        "database_unavailable", "The database is temporarily unavailable. Try again later.");

    /// <summary>
    /// Returns true and the mapped error for an optimistic concurrency failure, a unique violation (23505)
    /// or a transient fault (including an exhausted retry). Other exceptions are not mapped.
    /// </summary>
    public static bool TryMap(Exception exception, [NotNullWhen(true)] out Error? error)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case DbUpdateConcurrencyException:
                    error = ConcurrencyConflict;
                    return true;
                case PostgresException { SqlState: UniqueViolationSqlState }:
                    error = UniqueViolation;
                    return true;
                case RetryLimitExceededException:
                case NpgsqlException { IsTransient: true }:
                case TimeoutException:
                    error = Unavailable;
                    return true;
                default:
                    break;
            }
        }

        error = null;
        return false;
    }
}
