using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.ConfirmOrder;

/// <summary>Confirms a pending order (staff).</summary>
/// <param name="OrderId">The order.</param>
internal sealed record ConfirmOrderCommand(Guid OrderId) : ICommand<OrderResponse>;
