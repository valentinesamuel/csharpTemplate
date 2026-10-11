using Acme.Service.BuildingBlocks.Messaging;

namespace Acme.Service.Modules.Auth.Notifications;

/// <summary>
/// A confirmation email should be sent to <see cref="UserId"/>. Internal to the Auth module (produced and consumed
/// here, so not in Contracts), written to the Auth outbox in the same transaction as the change that requires it
/// (INV-08). It carries only the user id: the confirmation token is generated when the email is sent and is never
/// stored in the outbox (INV-28).
/// </summary>
/// <param name="Id">Event id (Guid v7).</param>
/// <param name="OccurredAt">When the email was requested (UTC).</param>
/// <param name="UserId">The user whose address must be confirmed.</param>
[IntegrationEvent("auth.email-verification-requested.v1")]
internal sealed record EmailVerificationRequested(Guid Id, DateTimeOffset OccurredAt, Guid UserId) : IIntegrationEvent
{
    public static EmailVerificationRequested For(Guid userId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var now = timeProvider.GetUtcNow();
        return new EmailVerificationRequested(Guid.CreateVersion7(now), now, userId);
    }
}
