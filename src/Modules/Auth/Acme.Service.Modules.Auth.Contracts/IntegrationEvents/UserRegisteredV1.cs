using Acme.Service.BuildingBlocks.Messaging;

namespace Acme.Service.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>A user registered an account. Published through the Auth outbox (INV-08).</summary>
/// <param name="Id">Event id (Guid v7).</param>
/// <param name="OccurredAt">When the user registered (UTC).</param>
/// <param name="UserId">The new user's id.</param>
/// <param name="Email">The new user's email address.</param>
/// <param name="DisplayName">The display name, when one was given.</param>
[IntegrationEvent("auth.user-registered.v1")]
public sealed record UserRegisteredV1(Guid Id, DateTimeOffset OccurredAt, Guid UserId, string Email, string? DisplayName) : IIntegrationEvent;
