using System.Globalization;
using Acme.Service.BuildingBlocks.Paging;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Microsoft.AspNetCore.Http;

namespace Acme.Service.BuildingBlocks.Web.Paging;

/// <summary>
/// Binds the list query parameters <c>page</c>, <c>pageSize</c> and <c>sort</c> (plan §0.5). Non-integer or
/// repeated paging values fail with <see cref="InvalidPagingCode"/>; out-of-range numbers are clamped by
/// <see cref="PageRequest"/>. Sort is resolved against the endpoint's <see cref="SortMap{T}"/>; a malformed or
/// unknown field fails with <see cref="SortSpec.InvalidSortCode"/>. Failures map to 400 via
/// <see cref="Results.ResultHttpExtensions.ToHttpResult(Result)"/>.
/// </summary>
public static class ListQueryBinding
{
    /// <summary>Page number query parameter (1-based).</summary>
    public const string PageParameter = "page";

    /// <summary>Page size query parameter.</summary>
    public const string PageSizeParameter = "pageSize";

    /// <summary>Error code for a malformed <c>page</c> or <c>pageSize</c>.</summary>
    public const string InvalidPagingCode = "invalid_paging";

    /// <summary>Binds paging and sort from <paramref name="request"/>'s query string.</summary>
    public static Result<ListQuery<T>> BindListQuery<T>(this HttpRequest request, SortMap<T> sortMap)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sortMap);

        var query = request.Query;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var page = ParseInt(query, PageParameter, errors);
        var pageSize = ParseInt(query, PageSizeParameter, errors);
        if (errors.Count > 0)
        {
            return Result.Failure<ListQuery<T>>(
                Error.Validation(InvalidPagingCode, "The paging parameters are invalid.", errors));
        }

        var sortValues = query[SortSpec.ParameterName];
        if (sortValues.Count > 1)
        {
            const string message = "Sort must be given at most once.";
            return Result.Failure<ListQuery<T>>(Error.Validation(
                SortSpec.InvalidSortCode,
                message,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [SortSpec.ParameterName] = [message] }));
        }

        var sort = sortMap.Resolve(sortValues.Count == 1 ? sortValues[0] : null);
        if (sort.IsFailure)
        {
            return Result.Failure<ListQuery<T>>(sort.Error);
        }

        return Result.Success(new ListQuery<T>(new PageRequest(page, pageSize), sort.Value));
    }

    private static int? ParseInt(IQueryCollection query, string name, Dictionary<string, string[]> errors)
    {
        var values = query[name];
        if (values.Count == 0)
        {
            return null;
        }

        if (values.Count == 1 && int.TryParse(values[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        errors[name] = [$"'{name}' must be a single integer."];
        return null;
    }
}

/// <summary>A bound list request: clamped paging plus whitelisted sort keys.</summary>
/// <typeparam name="T">Entity type.</typeparam>
/// <param name="Page">Paging.</param>
/// <param name="Sort">Sort keys in priority order (empty when none were requested).</param>
public sealed record ListQuery<T>(PageRequest Page, IReadOnlyList<SortExpression<T>> Sort)
    where T : class;
