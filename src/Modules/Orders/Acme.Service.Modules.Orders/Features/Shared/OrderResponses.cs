using Acme.Service.Modules.Orders.Domain;

namespace Acme.Service.Modules.Orders.Features.Shared;

/// <summary>An order with its lines, as returned by every endpoint that returns one order.</summary>
/// <param name="Id">Order id.</param>
/// <param name="CustomerId">The customer (user id) who placed it.</param>
/// <param name="Status">Pending, Confirmed, Cancelled or Expired.</param>
/// <param name="Currency">ISO 4217 currency code.</param>
/// <param name="Total">Sum of the line totals.</param>
/// <param name="Lines">The lines, in submission order.</param>
/// <param name="CreatedAt">When it was placed (UTC).</param>
/// <param name="UpdatedAt">When it last changed (UTC).</param>
/// <param name="Version">Concurrency token; send it back with <c>PUT</c>.</param>
internal sealed record OrderResponse(
    Guid Id,
    Guid CustomerId,
    string Status,
    string Currency,
    decimal Total,
    IReadOnlyList<OrderLineResponse> Lines,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version)
{
    public static OrderResponse From(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new OrderResponse(
            order.Id,
            order.CustomerId,
            order.Status.ToString(),
            order.Currency,
            order.Total,
            [.. order.Lines.OrderBy(l => l.LineNumber).Select(l => new OrderLineResponse(l.Sku, l.Quantity, l.UnitPrice, l.LineTotal))],
            order.CreatedAt,
            order.UpdatedAt,
            order.Version);
    }
}

/// <summary>One line of an <see cref="OrderResponse"/>.</summary>
/// <param name="Sku">The stock keeping unit.</param>
/// <param name="Quantity">Quantity.</param>
/// <param name="UnitPrice">Unit price.</param>
/// <param name="LineTotal">Quantity times unit price.</param>
internal sealed record OrderLineResponse(string Sku, int Quantity, decimal UnitPrice, decimal LineTotal);

/// <summary>A line as sent by the client. Every field is required; nullable so a missing one is a validation error.</summary>
/// <param name="Sku">The stock keeping unit, 1 to 64 characters.</param>
/// <param name="Quantity">1 to 10000.</param>
/// <param name="UnitPrice">0 to 1000000000, at most two decimals.</param>
internal sealed record OrderLineRequest(string? Sku, int? Quantity, decimal? UnitPrice)
{
    /// <summary>The domain drafts of validated lines.</summary>
    public static IReadOnlyList<OrderLineDraft> ToDrafts(IReadOnlyList<OrderLineRequest> lines) =>
        [.. lines.Select(l => new OrderLineDraft(l.Sku!, l.Quantity!.Value, l.UnitPrice!.Value))];
}
