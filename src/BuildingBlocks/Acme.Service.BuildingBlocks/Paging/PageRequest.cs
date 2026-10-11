namespace Acme.Service.BuildingBlocks.Paging;

/// <summary>
/// A 1-based page request (plan §0.5). Out-of-range values are clamped, never rejected:
/// page &lt; 1 becomes 1; a missing or non-positive page size becomes <see cref="DefaultPageSize"/>;
/// a page size above <see cref="MaxPageSize"/> becomes <see cref="MaxPageSize"/>.
/// </summary>
public sealed record PageRequest
{
    /// <summary>Page size used when none is supplied.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>Largest accepted page size.</summary>
    public const int MaxPageSize = 100;

    // Keeps Skip within int range for every accepted page size.
    private const int MaxPage = (int.MaxValue / MaxPageSize) + 1;

    /// <summary>Creates a clamped page request.</summary>
    public PageRequest(int? page = null, int? pageSize = null)
    {
        Page = page is null or < 1 ? 1 : Math.Min(page.Value, MaxPage);
        PageSize = pageSize is null or < 1 ? DefaultPageSize : Math.Min(pageSize.Value, MaxPageSize);
    }

    /// <summary>The first page with the default page size.</summary>
    public static PageRequest Default { get; } = new();

    /// <summary>1-based page number.</summary>
    public int Page { get; }

    /// <summary>Number of items per page (1 to <see cref="MaxPageSize"/>).</summary>
    public int PageSize { get; }

    /// <summary>Number of items to skip.</summary>
    public int Skip => (Page - 1) * PageSize;
}
