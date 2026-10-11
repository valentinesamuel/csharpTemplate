using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.Modules.Orders.Domain;
using Microsoft.EntityFrameworkCore;

namespace Acme.Service.Modules.Orders.Persistence.Repositories;

/// <summary>
/// <see cref="IOrderRepository"/> on EF Core: the generic members come from <see cref="EfRepository{TContext,TEntity,TId}"/>
/// (tracked, for commands); the read-only members add <c>AsNoTracking</c> for queries. Lines are loaded with their
/// order (owned), and the soft-delete query filter applies to every member.
/// </summary>
internal sealed class OrderRepository(OrdersDbContext context)
    : EfRepository<OrdersDbContext, Order, Guid>(context), IOrderRepository
{
    /// <inheritdoc />
    /// <remarks>A query rather than <c>Find</c>, so the soft-delete filter visibly applies; the order is tracked.</remarks>
    public override Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        QueryAsync(token => Set.SingleOrDefaultAsync(o => o.Id == id, token), cancellationToken);

    public Task<Order?> GetForReadAsync(Guid id, CancellationToken cancellationToken) =>
        QueryAsync(token => Set.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, token), cancellationToken);

    public Task<IReadOnlyList<Order>> ListForReadAsync(Specification<Order> specification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return QueryAsync<IReadOnlyList<Order>>(
            async token => await Apply(specification).AsNoTracking().ToListAsync(token).ConfigureAwait(false),
            cancellationToken);
    }
}
