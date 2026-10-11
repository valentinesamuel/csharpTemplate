using System.Text;
using Acme.Service.BuildingBlocks.Locking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Acme.Service.BuildingBlocks.Persistence.Locking;

/// <summary>
/// <see cref="IDistributedLock"/> on Postgres session-level advisory locks (D-06), selected with
/// <c>Locks:Provider=Postgres</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each acquisition opens a dedicated, non-pooled connection (<c>ConnectionStrings:Postgres</c>) and
/// holds it for the lock's lifetime, so the lock can never leak into a pooled connection.</item>
/// <item>The lock key is <see cref="ComputeKey"/>: the 64-bit FNV-1a hash of the UTF-8 name, reinterpreted as a
/// signed <c>bigint</c>. It is stable across processes, platforms and releases; changing it would let old and
/// new replicas hold the "same" lock at once.</item>
/// <item><c>pg_try_advisory_lock</c> is retried until the timeout (zero means a single try).</item>
/// <item>Released by disposing the handle (<c>pg_advisory_unlock</c>, then the connection is closed), or by the
/// server when the connection is lost.</item>
/// </list>
/// </remarks>
internal sealed partial class PostgresDistributedLock(
    IOptions<PostgresLockOptions> options,
    TimeProvider timeProvider,
    ILogger<PostgresDistributedLock> logger) : IDistributedLock
{
    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    private static readonly TimeSpan _minPollDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan _maxPollDelay = TimeSpan.FromSeconds(1);

    /// <summary>The advisory lock key of <paramref name="name"/>: FNV-1a 64 over its UTF-8 bytes, as a signed 64-bit value.</summary>
    public static long ComputeKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var hash = FnvOffsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            hash ^= b;
            hash = unchecked(hash * FnvPrime);
        }

        return unchecked((long)hash);
    }

    /// <summary>
    /// Waits (no timeout; cancellable) for the advisory lock <paramref name="name"/> on a dedicated non-pooled
    /// connection. Used by the migration runner (WH-08 fallback), which must serialize concurrent runs.
    /// </summary>
    internal static async Task<IAsyncDisposable> AcquireAsync(string connectionString, string name, ILogger logger, CancellationToken cancellationToken)
    {
        var key = ComputeKey(name);
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection);
            await using (command.ConfigureAwait(false))
            {
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            LogAcquired(logger, name);
            return new Handle(connection, key, name, logger);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        var key = ComputeKey(name);
        var connectionString = new NpgsqlConnectionStringBuilder(options.Value.ConnectionString) { Pooling = false }.ConnectionString;
        var connection = new NpgsqlConnection(connectionString);
        var acquired = false;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var deadline = timeProvider.GetUtcNow() + timeout;
            var pollDelay = _minPollDelay;
            while (true)
            {
                if (await TryLockAsync(connection, key, cancellationToken).ConfigureAwait(false))
                {
                    acquired = true;
                    LogAcquired(logger, name);
                    return new Handle(connection, key, name, logger);
                }

                var remaining = deadline - timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    return null;
                }

                await Task.Delay(remaining < pollDelay ? remaining : pollDelay, timeProvider, cancellationToken).ConfigureAwait(false);
                pollDelay = TimeSpan.FromTicks(Math.Min(pollDelay.Ticks * 2, _maxPollDelay.Ticks));
            }
        }
        finally
        {
            if (!acquired)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> TryLockAsync(NpgsqlConnection connection, long key, CancellationToken cancellationToken)
    {
        var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("key", key);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Acquired distributed lock {LockName}")]
    private static partial void LogAcquired(ILogger logger, string lockName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Released distributed lock {LockName}")]
    private static partial void LogReleased(ILogger logger, string lockName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not unlock distributed lock {LockName} explicitly; closing its connection releases it")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, string lockName);

    /// <summary>Holds the lock's connection; disposing unlocks and closes it. Idempotent.</summary>
    private sealed class Handle(NpgsqlConnection connection, long key, string name, ILogger logger) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                    await using (command.ConfigureAwait(false))
                    {
                        command.Parameters.AddWithValue("key", key);
                        await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }

                LogReleased(logger, name);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                // The connection is broken or closed: the server has released (or will release) the session lock.
                LogReleaseFailed(logger, ex, name);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
