using System.Linq.Expressions;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;

namespace Acme.Service.BuildingBlocks.Paging;

/// <summary>
/// Per-endpoint whitelist of sortable fields (plan §0.5). Maps public field names (case-sensitive,
/// camelCase) to entity members; any other field is rejected with <see cref="ErrorType.Validation"/> (400).
/// </summary>
/// <typeparam name="T">Entity type.</typeparam>
public sealed class SortMap<T>
    where T : class
{
    private readonly Dictionary<string, Expression<Func<T, object?>>> _fields = new(StringComparer.Ordinal);

    /// <summary>The whitelisted field names.</summary>
    public IReadOnlyCollection<string> Fields => _fields.Keys;

    /// <summary>Whitelists <paramref name="field"/>, sorting by <paramref name="keySelector"/>.</summary>
    public SortMap<T> Add(string field, Expression<Func<T, object?>> keySelector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(keySelector);

        if (!_fields.TryAdd(field, keySelector))
        {
            throw new ArgumentException($"Sort field '{field}' is already mapped.", nameof(field));
        }

        return this;
    }

    /// <summary>
    /// Parses <paramref name="sort"/> (for example <c>-createdAt,total</c>) and resolves every term
    /// against the whitelist. Null or whitespace yields an empty list.
    /// </summary>
    public Result<IReadOnlyList<SortExpression<T>>> Resolve(string? sort)
    {
        var parsed = SortSpec.Parse(sort);
        if (parsed.IsFailure)
        {
            return Result.Failure<IReadOnlyList<SortExpression<T>>>(parsed.Error);
        }

        var resolved = new List<SortExpression<T>>(parsed.Value.Count);
        foreach (var spec in parsed.Value)
        {
            if (!_fields.TryGetValue(spec.Field, out var keySelector))
            {
                return Result.Failure<IReadOnlyList<SortExpression<T>>>(
                    SortSpec.Invalid($"Unknown sort field '{spec.Field}'. Allowed: {string.Join(", ", _fields.Keys)}."));
            }

            resolved.Add(new SortExpression<T>(keySelector, spec.Descending));
        }

        return Result.Success<IReadOnlyList<SortExpression<T>>>(resolved);
    }
}
