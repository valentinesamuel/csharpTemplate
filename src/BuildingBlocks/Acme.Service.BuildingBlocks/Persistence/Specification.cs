using System.Linq.Expressions;
using Acme.Service.BuildingBlocks.Paging;

namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>
/// Provider-neutral query description: filter criteria, ordered sort keys and paging. Persistence
/// adapters translate it (EF Core today, another store later; D-01).
/// </summary>
/// <typeparam name="T">Entity type.</typeparam>
public class Specification<T>
    where T : class
{
    private readonly List<SortExpression<T>> _orderBy = [];

    /// <summary>Filter predicate, or null for all entities.</summary>
    public Expression<Func<T, bool>>? Criteria { get; private set; }

    /// <summary>Sort keys in priority order.</summary>
    public IReadOnlyList<SortExpression<T>> OrderBy => _orderBy;

    /// <summary>Number of entities to skip, or null for none.</summary>
    public int? Skip { get; private set; }

    /// <summary>Maximum number of entities to return, or null for no limit.</summary>
    public int? Take { get; private set; }

    /// <summary>Adds a filter. Multiple calls are combined with logical AND.</summary>
    public Specification<T> Where(Expression<Func<T, bool>> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (Criteria is null)
        {
            Criteria = criteria;
            return this;
        }

        var parameter = Criteria.Parameters[0];
        var right = new ParameterReplacer(criteria.Parameters[0], parameter).Visit(criteria.Body);
        Criteria = Expression.Lambda<Func<T, bool>>(Expression.AndAlso(Criteria.Body, right), parameter);
        return this;
    }

    /// <summary>Appends a sort key.</summary>
    public Specification<T> AddOrderBy(Expression<Func<T, object?>> keySelector, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _orderBy.Add(new SortExpression<T>(keySelector, descending));
        return this;
    }

    /// <summary>Appends sort keys resolved by a <see cref="SortMap{T}"/>.</summary>
    public Specification<T> AddOrderBy(IEnumerable<SortExpression<T>> sort)
    {
        ArgumentNullException.ThrowIfNull(sort);
        _orderBy.AddRange(sort);
        return this;
    }

    /// <summary>Applies 1-based paging from <paramref name="page"/>.</summary>
    public Specification<T> ApplyPaging(PageRequest page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Skip = page.Skip;
        Take = page.PageSize;
        return this;
    }

    private sealed class ParameterReplacer(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == source ? target : base.VisitParameter(node);
    }
}

/// <summary>A sort key of a <see cref="Specification{T}"/>.</summary>
/// <typeparam name="T">Entity type.</typeparam>
/// <param name="KeySelector">Member to sort by.</param>
/// <param name="Descending">True for descending order.</param>
public sealed record SortExpression<T>(Expression<Func<T, object?>> KeySelector, bool Descending)
    where T : class;
