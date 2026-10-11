using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.BuildingBlocks.UseCases.Behaviors;

/// <summary>
/// Innermost behaviour. For <see cref="ICommand{TOut}"/> inputs only, runs the rest of the pipeline inside
/// the owning module's keyed <see cref="IUnitOfWork"/> (D-03, D-04): a successful result commits, a failed
/// result or an exception rolls back. Retries on transient faults are allowed only for
/// <see cref="IRetryableCommand"/> (D-28, INV-26). Queries and other inputs pass straight through.
/// </summary>
/// <remarks>
/// <see cref="IModuleUnitOfWorkResolver"/> is resolved lazily, and only for commands, so that queries and
/// modules without persistence do not require a unit-of-work implementation to be registered.
/// </remarks>
/// <typeparam name="TIn">Input type.</typeparam>
/// <typeparam name="TOut">Success value type.</typeparam>
public sealed class TransactionBehavior<TIn, TOut>(
    UseCaseModuleRegistry moduleRegistry,
    IServiceProvider serviceProvider) : IUseCaseBehavior<TIn, TOut>
    where TIn : notnull
{
    /// <inheritdoc />
    public async Task<Result<TOut>> HandleAsync(TIn input, UseCaseNext<TOut> continuation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(continuation);

        if (input is not ICommand<TOut>)
        {
            return await continuation(cancellationToken).ConfigureAwait(false);
        }

        var moduleKey = moduleRegistry.GetModuleKey(typeof(TIn));
        var unitOfWork = serviceProvider.GetRequiredService<IModuleUnitOfWorkResolver>().Resolve(moduleKey);
        var retryOnTransientFailure = input is IRetryableCommand;

        // The operation may run more than once when retries are allowed; keep the last attempt's result.
        Result<TOut>? useCaseResult = null;
        var transactionResult = await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                useCaseResult = await continuation(token).ConfigureAwait(false);
                return useCaseResult.IsSuccess ? Result.Success() : Result.Failure(useCaseResult.Error);
            },
            retryOnTransientFailure,
            cancellationToken).ConfigureAwait(false);

        if (useCaseResult is null)
        {
            // The unit of work failed before the use case ran (for example, it could not open a transaction).
            return transactionResult.IsFailure
                ? Result.Failure<TOut>(transactionResult.Error)
                : throw new InvalidOperationException("The unit of work completed without invoking the use case.");
        }

        if (useCaseResult.IsFailure)
        {
            return useCaseResult;
        }

        // The use case succeeded but the commit failed (for example, a transient fault surfaced as Unavailable).
        return transactionResult.IsFailure ? Result.Failure<TOut>(transactionResult.Error) : useCaseResult;
    }
}
