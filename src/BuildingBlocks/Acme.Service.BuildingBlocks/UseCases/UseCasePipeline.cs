using Acme.Service.BuildingBlocks.Results;

namespace Acme.Service.BuildingBlocks.UseCases;

/// <summary>
/// Open-generic <see cref="IUseCaseExecutor{TIn, TOut}"/> (D-03). Wraps the use case in the
/// registered behaviours, first-registered outermost. Exceptions are never converted to results.
/// </summary>
/// <typeparam name="TIn">Input type.</typeparam>
/// <typeparam name="TOut">Success value type.</typeparam>
public sealed class UseCasePipeline<TIn, TOut>(
    IUseCase<TIn, TOut> useCase,
    IEnumerable<IUseCaseBehavior<TIn, TOut>> behaviors) : IUseCaseExecutor<TIn, TOut>
    where TIn : notnull
{
    private readonly IUseCaseBehavior<TIn, TOut>[] _behaviors = [.. behaviors];

    /// <inheritdoc />
    public Task<Result<TOut>> ExecuteAsync(TIn input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        UseCaseNext<TOut> next = token => useCase.ExecuteAsync(input, token);

        for (var i = _behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = _behaviors[i];
            var inner = next;
            next = token => behavior.HandleAsync(input, inner, token);
        }

        return next(cancellationToken);
    }
}
