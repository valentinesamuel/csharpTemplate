namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>
/// Provider-neutral repository port for an aggregate root. Changes are persisted by the module's
/// <see cref="IUnitOfWork"/>, not by the repository.
/// </summary>
/// <typeparam name="TEntity">Aggregate root type.</typeparam>
/// <typeparam name="TId">Identifier type.</typeparam>
public interface IRepository<TEntity, in TId>
    where TEntity : class
    where TId : notnull
{
    /// <summary>Returns the entity with <paramref name="id"/>, or null.</summary>
    Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken);

    /// <summary>Tracks a new entity for insertion.</summary>
    Task AddAsync(TEntity entity, CancellationToken cancellationToken);

    /// <summary>Tracks an entity for deletion.</summary>
    void Remove(TEntity entity);

    /// <summary>Returns the entities matching <paramref name="specification"/> (criteria, sort, paging).</summary>
    Task<IReadOnlyList<TEntity>> ListAsync(Specification<TEntity> specification, CancellationToken cancellationToken);

    /// <summary>Counts the entities matching the criteria of <paramref name="specification"/> (sort and paging ignored).</summary>
    Task<int> CountAsync(Specification<TEntity> specification, CancellationToken cancellationToken);
}
