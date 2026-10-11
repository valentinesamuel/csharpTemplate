using Acme.Service.BuildingBlocks.Persistence;

namespace Acme.Service.Modules.Orders.Domain;

/// <summary>
/// The order store port (D-01). The inherited members track what they return, for changes committed by the Orders unit
/// of work; the read-only members return untracked snapshots for queries. Deleted orders are never returned.
/// </summary>
internal interface IOrderRepository : IRepository<Order, Guid>
{
    /// <summary>Returns an untracked snapshot of the order <paramref name="id"/>, or null.</summary>
    Task<Order?> GetForReadAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns untracked snapshots of the orders matching <paramref name="specification"/>.</summary>
    Task<IReadOnlyList<Order>> ListForReadAsync(Specification<Order> specification, CancellationToken cancellationToken);
}
