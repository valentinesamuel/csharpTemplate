using Acme.Service.BuildingBlocks.Messaging;

namespace Acme.Service.Modules.Orders.Contracts.IntegrationEvents;

/// <summary>A customer placed an order. Published through the Orders outbox (INV-08).</summary>
/// <param name="Id">Event id (Guid v7).</param>
/// <param name="OccurredAt">When the order was placed (UTC).</param>
/// <param name="OrderId">The new order's id.</param>
/// <param name="CustomerId">The customer (user id) who placed the order.</param>
/// <param name="Currency">ISO 4217 currency code of the order.</param>
/// <param name="Total">Order total, in <paramref name="Currency"/>.</param>
[IntegrationEvent("orders.order-placed.v1")]
public sealed record OrderPlacedV1(Guid Id, DateTimeOffset OccurredAt, Guid OrderId, Guid CustomerId, string Currency, decimal Total) : IIntegrationEvent;
