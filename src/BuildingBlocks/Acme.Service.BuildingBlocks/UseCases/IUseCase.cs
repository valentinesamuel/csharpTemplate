using Acme.Service.BuildingBlocks.Results;

namespace Acme.Service.BuildingBlocks.UseCases;

/// <summary>
/// A single application use case. Input records implement <see cref="ICommand{TOut}"/> (transactional)
/// or <see cref="IQuery{TOut}"/> (read-only). Callers go through <see cref="IUseCaseExecutor{TIn, TOut}"/>
/// so the behaviour pipeline runs.
/// </summary>
/// <typeparam name="TIn">Input type.</typeparam>
/// <typeparam name="TOut">Success value type.</typeparam>
public interface IUseCase<in TIn, TOut>
    where TIn : notnull
{
    /// <summary>Executes the use case. Expected failures are returned as a failed result; unexpected ones throw.</summary>
    Task<Result<TOut>> ExecuteAsync(TIn input, CancellationToken cancellationToken);
}
