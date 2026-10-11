using Acme.Service.BuildingBlocks.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>
/// <see cref="IUnitOfWork"/> over one module context (INV-07), registered keyed by the module name (D-04).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A successful operation result saves and commits; a failed result or an exception rolls back.</item>
/// <item>Only when <c>retryOnTransientFailure</c> is true is the whole transaction re-run under the Npgsql
/// retrying execution strategy (D-28, INV-26). Otherwise a transient fault becomes an
/// <see cref="ErrorType.Unavailable"/> failure (503).</item>
/// <item>Optimistic concurrency failures and unique violations (23505) become <see cref="ErrorType.Conflict"/>.</item>
/// <item>When a transaction is already open on the context, the operation joins it: changes are saved
/// but the outer owner commits.</item>
/// </list>
/// </remarks>
/// <typeparam name="TContext">The module context.</typeparam>
internal sealed partial class EfUnitOfWork<TContext>(TContext context, ILogger<EfUnitOfWork<TContext>> logger) : IUnitOfWork
    where TContext : ModuleDbContext
{
    /// <inheritdoc />
    public async Task<Result> ExecuteInTransactionAsync(
        Func<CancellationToken, Task<Result>> operation,
        bool retryOnTransientFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            if (context.Database.CurrentTransaction is not null)
            {
                return await JoinAsync(operation, cancellationToken).ConfigureAwait(false);
            }

            if (!retryOnTransientFailure)
            {
                return await RunAttemptAsync(operation, cancellationToken).ConfigureAwait(false);
            }

            var attempts = 0;
            var strategy = NpgsqlContextOptions.CreateRetryingStrategy(context);
            return await strategy.ExecuteAsync(
                async token =>
                {
                    if (attempts++ > 0)
                    {
                        LogRetrying(logger, typeof(TContext).Name, attempts);
                    }

                    return await RunAttemptAsync(operation, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && DatabaseErrors.TryMap(ex, out var error))
        {
            LogMappedFailure(logger, ex, typeof(TContext).Name, error.Code);
            return Result.Failure(error);
        }
    }

    /// <inheritdoc />
    public Task SaveChangesAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);

    private async Task<Result> RunAttemptAsync(Func<CancellationToken, Task<Result>> operation, CancellationToken cancellationToken)
    {
        try
        {
            IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var result = await operation(cancellationToken).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    context.ChangeTracker.Clear();
                    return result;
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
        }
        catch
        {
            // Disposing the transaction rolled it back; drop the tracked changes of the failed attempt so a
            // retry (or a later SaveChanges in the same scope) does not persist them.
            context.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<Result> JoinAsync(Func<CancellationToken, Task<Result>> operation, CancellationToken cancellationToken)
    {
        var result = await operation(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retrying transaction on {Context} after a transient fault (attempt {Attempt})")]
    private static partial void LogRetrying(ILogger logger, string context, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transaction on {Context} failed and was mapped to {ErrorCode}")]
    private static partial void LogMappedFailure(ILogger logger, Exception exception, string context, string errorCode);
}
