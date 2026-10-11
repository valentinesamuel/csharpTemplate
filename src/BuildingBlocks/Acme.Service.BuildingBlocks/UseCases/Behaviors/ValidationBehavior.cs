using Acme.Service.BuildingBlocks.Results;
using FluentValidation;

namespace Acme.Service.BuildingBlocks.UseCases.Behaviors;

/// <summary>
/// Runs every registered FluentValidation <see cref="IValidator{T}"/> for the input asynchronously (D-18).
/// Any failure short-circuits with an <see cref="ErrorType.Validation"/> error whose details are keyed by
/// property name; the use case is not invoked.
/// </summary>
/// <typeparam name="TIn">Input type.</typeparam>
/// <typeparam name="TOut">Success value type.</typeparam>
public sealed class ValidationBehavior<TIn, TOut>(IEnumerable<IValidator<TIn>> validators) : IUseCaseBehavior<TIn, TOut>
    where TIn : notnull
{
    /// <summary>Error code of the validation failure.</summary>
    public const string ErrorCode = "validation_failed";

    private readonly IValidator<TIn>[] _validators = [.. validators];

    /// <inheritdoc />
    public async Task<Result<TOut>> HandleAsync(TIn input, UseCaseNext<TOut> continuation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(continuation);

        if (_validators.Length == 0)
        {
            return await continuation(cancellationToken).ConfigureAwait(false);
        }

        var context = new ValidationContext<TIn>(input);
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return await continuation(cancellationToken).ConfigureAwait(false);
        }

        var details = failures
            .GroupBy(f => f.PropertyName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        return Result.Failure<TOut>(Error.Validation(ErrorCode, "One or more validation errors occurred.", details));
    }
}
