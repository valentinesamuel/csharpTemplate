using Acme.Service.BuildingBlocks.Results;

namespace Acme.Service.BuildingBlocks.UseCases;

/// <summary>
/// Entry point for running a use case through its behaviour pipeline. Endpoints, jobs and event
/// consumers depend on this rather than on <see cref="IUseCase{TIn, TOut}"/> directly.
/// </summary>
/// <typeparam name="TIn">Input type.</typeparam>
/// <typeparam name="TOut">Success value type.</typeparam>
public interface IUseCaseExecutor<in TIn, TOut>
    where TIn : notnull
{
    /// <summary>Runs the pipeline and the use case.</summary>
    Task<Result<TOut>> ExecuteAsync(TIn input, CancellationToken cancellationToken);
}
