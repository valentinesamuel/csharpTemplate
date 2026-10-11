using Microsoft.EntityFrameworkCore;

namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IRepository{TEntity,TId}"/> that translates a provider-neutral
/// <see cref="Specification{T}"/> (D-01). Register with
/// <see cref="ModulePersistenceBuilder{TContext}.AddRepository{TEntity,TId}"/>, or derive for module-specific queries.
/// </summary>
/// <remarks>
/// Reads outside a transaction run under the retrying execution strategy (D-28: queries retry). Inside a
/// unit-of-work transaction they run once; the unit of work decides whether the whole transaction retries.
/// </remarks>
/// <typeparam name="TContext">The module context that maps <typeparamref name="TEntity"/>.</typeparam>
/// <typeparam name="TEntity">Aggregate root type.</typeparam>
/// <typeparam name="TId">Primary key type.</typeparam>
public class EfRepository<TContext, TEntity, TId>(TContext context) : IRepository<TEntity, TId>
    where TContext : ModuleDbContext
    where TEntity : class
    where TId : notnull
{
    /// <summary>The module context.</summary>
    protected TContext Context { get; } = context;

    /// <summary>The entity set.</summary>
    protected DbSet<TEntity> Set => Context.Set<TEntity>();

    /// <inheritdoc />
    public virtual Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken) =>
        QueryAsync(token => Set.FindAsync([id], token).AsTask(), cancellationToken);

    /// <inheritdoc />
    public virtual async Task AddAsync(TEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await Set.AddAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual void Remove(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Set.Remove(entity);
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<TEntity>> ListAsync(Specification<TEntity> specification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return QueryAsync<IReadOnlyList<TEntity>>(
            async token => await Apply(specification).ToListAsync(token).ConfigureAwait(false),
            cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<int> CountAsync(Specification<TEntity> specification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return QueryAsync(token => ApplyCriteria(Set, specification).CountAsync(token), cancellationToken);
    }

    /// <summary>
    /// Runs a read. Outside a transaction it is retried on transient faults; inside one it runs once.
    /// </summary>
    protected Task<T> QueryAsync<T>(Func<CancellationToken, Task<T>> query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Context.Database.CurrentTransaction is not null
            ? query(cancellationToken)
            : NpgsqlContextOptions.CreateRetryingStrategy(Context).ExecuteAsync(query, cancellationToken);
    }

    /// <summary>Applies criteria, sort keys (in order) and paging.</summary>
    protected IQueryable<TEntity> Apply(Specification<TEntity> specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var query = ApplyCriteria(Set, specification);

        IOrderedQueryable<TEntity>? ordered = null;
        foreach (var sort in specification.OrderBy)
        {
            ordered = (ordered, sort.Descending) switch
            {
                (null, false) => query.OrderBy(sort.KeySelector),
                (null, true) => query.OrderByDescending(sort.KeySelector),
                (_, false) => ordered.ThenBy(sort.KeySelector),
                (_, true) => ordered.ThenByDescending(sort.KeySelector),
            };
        }

        query = ordered ?? query;

        if (specification.Skip is { } skip)
        {
            query = query.Skip(skip);
        }

        if (specification.Take is { } take)
        {
            query = query.Take(take);
        }

        return query;
    }

    private static IQueryable<TEntity> ApplyCriteria(IQueryable<TEntity> query, Specification<TEntity> specification) =>
        specification.Criteria is null ? query : query.Where(specification.Criteria);
}
