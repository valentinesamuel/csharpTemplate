using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.CancelOrder;

/// <summary>Cancels a pending order: the caller's own, or any order for a manager.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Caller">The authenticated caller.</param>
internal sealed record CancelOrderCommand(Guid OrderId, OrdersCaller Caller) : ICommand<OrderResponse>;
