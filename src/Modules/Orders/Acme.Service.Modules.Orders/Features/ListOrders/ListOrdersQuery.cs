using Acme.Service.BuildingBlocks.Paging;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Domain;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.ListOrders;

/// <summary>
/// One page of orders: the caller's own, or every order for a manager; optionally filtered by status and creation time
/// (inclusive bounds, UTC).
/// </summary>
/// <param name="Caller">The authenticated caller.</param>
/// <param name="Status">Only orders in this status, when set.</param>
/// <param name="CreatedFrom">Only orders created at or after this instant, when set.</param>
/// <param name="CreatedTo">Only orders created at or before this instant, when set.</param>
/// <param name="Page">Paging.</param>
/// <param name="Sort">Whitelisted sort keys (<see cref="ListOrdersSort.Map"/>); newest first when empty.</param>
internal sealed record ListOrdersQuery(
    OrdersCaller Caller,
    OrderStatus? Status,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    PageRequest Page,
    IReadOnlyList<SortExpression<Order>> Sort) : IQuery<PagedResult<OrderSummaryResponse>>;

/// <summary>The sortable fields of the order list (§0.5): <c>createdAt</c>, <c>total</c> and <c>status</c>.</summary>
internal static class ListOrdersSort
{
    public static readonly SortMap<Order> Map = new SortMap<Order>()
        .Add("createdAt", o => o.CreatedAt)
        .Add("total", o => o.Total)
        .Add("status", o => o.Status);
}

/// <summary>An order in the list, without its lines.</summary>
/// <param name="Id">Order id.</param>
/// <param name="CustomerId">The customer (user id) who placed it.</param>
/// <param name="Status">Pending, Confirmed, Cancelled or Expired.</param>
/// <param name="Currency">ISO 4217 currency code.</param>
/// <param name="Total">Sum of the line totals.</param>
/// <param name="LineCount">Number of lines.</param>
/// <param name="CreatedAt">When it was placed (UTC).</param>
/// <param name="UpdatedAt">When it last changed (UTC).</param>
/// <param name="Version">Concurrency token.</param>
internal sealed record OrderSummaryResponse(
    Guid Id,
    Guid CustomerId,
    string Status,
    string Currency,
    decimal Total,
    int LineCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version)
{
    public static OrderSummaryResponse From(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new OrderSummaryResponse(
            order.Id, order.CustomerId, order.Status.ToString(), order.Currency, order.Total, order.Lines.Count, order.CreatedAt, order.UpdatedAt, order.Version);
    }
}
