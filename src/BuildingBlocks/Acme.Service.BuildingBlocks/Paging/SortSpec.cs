using Acme.Service.BuildingBlocks.Results;

namespace Acme.Service.BuildingBlocks.Paging;

/// <summary>
/// One parsed sort term of the query syntax <c>sort=-createdAt,total</c> (plan §0.5):
/// a leading <c>-</c> means descending, otherwise ascending.
/// </summary>
/// <param name="Field">Public (camelCase) field name.</param>
/// <param name="Descending">True for descending order.</param>
public sealed record SortSpec(string Field, bool Descending)
{
    /// <summary>Error code returned for any malformed or unknown sort expression.</summary>
    public const string InvalidSortCode = "invalid_sort";

    /// <summary>Query parameter name used in validation details.</summary>
    public const string ParameterName = "sort";

    /// <summary>
    /// Parses a sort expression. Null or whitespace yields an empty list. Empty terms, a bare
    /// <c>-</c>, or a field listed twice yield an <see cref="ErrorType.Validation"/> failure.
    /// </summary>
    public static Result<IReadOnlyList<SortSpec>> Parse(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return Result.Success<IReadOnlyList<SortSpec>>([]);
        }

        var specs = new List<SortSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawTerm in sort.Split(','))
        {
            var term = rawTerm.Trim();
            var descending = term.StartsWith('-');
            var field = descending ? term[1..] : term;

            if (field.Length == 0)
            {
                return Invalid("Sort contains an empty field.");
            }

            if (!seen.Add(field))
            {
                return Invalid($"Sort field '{field}' is listed more than once.");
            }

            specs.Add(new SortSpec(field, descending));
        }

        return Result.Success<IReadOnlyList<SortSpec>>(specs);
    }

    internal static Error Invalid(string message) =>
        Error.Validation(
            InvalidSortCode,
            message,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [ParameterName] = [message] });
}
