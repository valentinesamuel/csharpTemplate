namespace Acme.Service.BuildingBlocks.Paging;

/// <summary>
/// The list envelope <c>{ items, page, pageSize, totalCount, totalPages }</c> (plan §0.5, INV-18).
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>Creates the envelope for one page of <paramref name="totalCount"/> items.</summary>
    public PagedResult(IReadOnlyList<T> items, PageRequest request, int totalCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);

        Items = items;
        Page = request.Page;
        PageSize = request.PageSize;
        TotalCount = totalCount;
        TotalPages = (int)(((long)totalCount + request.PageSize - 1) / request.PageSize);
    }

    /// <summary>Items on this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>1-based page number.</summary>
    public int Page { get; }

    /// <summary>Page size used.</summary>
    public int PageSize { get; }

    /// <summary>Total matching items across all pages.</summary>
    public int TotalCount { get; }

    /// <summary>Total number of pages (0 when there are no items).</summary>
    public int TotalPages { get; }
}
