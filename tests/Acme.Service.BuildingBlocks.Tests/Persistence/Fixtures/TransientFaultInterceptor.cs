using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;

/// <summary>
/// Fails the next <see cref="FailuresRemaining"/> write commands with a transient <see cref="NpgsqlException"/>
/// (an I/O failure, as during a failover), before they reach the server.
/// </summary>
internal sealed class TransientFaultInterceptor : DbCommandInterceptor
{
    private int _failuresRemaining;
    private int _attempts;

    public int FailuresRemaining
    {
        get => Volatile.Read(ref _failuresRemaining);
        set => Volatile.Write(ref _failuresRemaining, value);
    }

    /// <summary>Write commands seen (failed or not).</summary>
    public int WriteAttempts => Volatile.Read(ref _attempts);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("INSERT", StringComparison.Ordinal)
            || command.CommandText.Contains("UPDATE", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _attempts);
            if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
            {
                throw new NpgsqlException("Simulated failover.", new IOException("Connection reset."));
            }
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
