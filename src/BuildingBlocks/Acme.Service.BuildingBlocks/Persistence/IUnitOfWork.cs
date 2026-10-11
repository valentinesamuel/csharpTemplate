using Acme.Service.BuildingBlocks.Results;

namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>
/// Transaction boundary of exactly one module (INV-07). Registered as a keyed scoped service keyed
/// by the module name (D-04). The outbox of the same module is flushed in the same transaction (INV-08).
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Runs <paramref name="operation"/> in a database transaction. Changes are saved and committed when
    /// it returns a successful result, and rolled back when it returns a failure or throws.
    /// </summary>
    /// <param name="operation">The work to run; it may be invoked more than once when retries are allowed.</param>
    /// <param name="retryOnTransientFailure">
    /// True only for queries and <see cref="UseCases.IRetryableCommand"/> commands (D-28, INV-26). When false,
    /// a transient fault is surfaced as an <see cref="ErrorType.Unavailable"/> failure or exception, never retried.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result> ExecuteInTransactionAsync(
        Func<CancellationToken, Task<Result>> operation,
        bool retryOnTransientFailure,
        CancellationToken cancellationToken);

    /// <summary>Saves pending changes without opening an explicit transaction.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
