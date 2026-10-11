using Acme.Service.BuildingBlocks.Results;

namespace Acme.Service.BuildingBlocks.UseCases;

/// <summary>Continuation that invokes the next behaviour in the pipeline, or the use case itself.</summary>
/// <typeparam name="TOut">Success value type.</typeparam>
public delegate Task<Result<TOut>> UseCaseNext<TOut>(CancellationToken cancellationToken);

/// <summary>
/// A cross-cutting step wrapped around every use case. Behaviours run in DI registration order;
/// the first registered is the outermost (Logging, Validation, Transaction, use case; D-03).
/// </summary>
/// <typeparam name="TIn">Input type.</typeparam>
/// <typeparam name="TOut">Success value type.</typeparam>
public interface IUseCaseBehavior<in TIn, TOut>
    where TIn : notnull
{
    /// <summary>Runs the behaviour. Call <paramref name="continuation"/> to continue the pipeline.</summary>
    Task<Result<TOut>> HandleAsync(TIn input, UseCaseNext<TOut> continuation, CancellationToken cancellationToken);
}
