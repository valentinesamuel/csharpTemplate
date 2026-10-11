using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.GetOrder;

/// <summary>Reads one order: the caller's own, or any order for a manager.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Caller">The authenticated caller.</param>
internal sealed record GetOrderQuery(Guid OrderId, OrdersCaller Caller) : IQuery<OrderResponse>;
