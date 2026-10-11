using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.UpdateOrderLines;

/// <summary>Replaces every line of a pending order, if it is still at <paramref name="Version"/>.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Caller">The authenticated caller: the owner, or a manager.</param>
/// <param name="Version">The concurrency token the caller last read.</param>
/// <param name="Lines">1 to 100 lines.</param>
internal sealed record UpdateOrderLinesCommand(Guid OrderId, OrdersCaller Caller, uint? Version, IReadOnlyList<OrderLineRequest>? Lines)
    : ICommand<OrderResponse>;

/// <summary>The body of <c>PUT /api/v1/orders/{id}</c>.</summary>
/// <param name="Version">The <c>version</c> of the order as last read.</param>
/// <param name="Lines">The new lines, 1 to 100.</param>
internal sealed record UpdateOrderLinesRequest(uint? Version, IReadOnlyList<OrderLineRequest>? Lines);
