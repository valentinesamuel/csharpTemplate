using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Orders.Features.Shared;

namespace Acme.Service.Modules.Orders.Features.DeleteOrder;

/// <summary>Soft-deletes an order (staff), in any status.</summary>
/// <param name="OrderId">The order.</param>
internal sealed record DeleteOrderCommand(Guid OrderId) : ICommand<Done>;
