using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.CreateOrder;

/// <summary>Places a pending order for the caller.</summary>
/// <param name="CustomerId">The caller (<c>sub</c>); becomes the order's customer.</param>
/// <param name="Currency">ISO 4217 code, three upper-case letters.</param>
/// <param name="Lines">1 to 100 lines.</param>
internal sealed record CreateOrderCommand(Guid CustomerId, string? Currency, IReadOnlyList<OrderLineRequest>? Lines) : ICommand<OrderResponse>;

/// <summary>The body of <c>POST /api/v1/orders</c>.</summary>
/// <param name="Currency">ISO 4217 code, three upper-case letters.</param>
/// <param name="Lines">1 to 100 lines.</param>
internal sealed record CreateOrderRequest(string? Currency, IReadOnlyList<OrderLineRequest>? Lines);
