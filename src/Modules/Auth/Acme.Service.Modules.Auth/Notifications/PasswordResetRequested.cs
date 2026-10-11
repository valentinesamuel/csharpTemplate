using Acme.Service.BuildingBlocks.Messaging;

namespace Acme.Service.Modules.Auth.Notifications;

/// <summary>
/// A password reset email should be sent to <see cref="UserId"/>. Internal to the Auth module (produced and consumed
/// here, so not in Contracts), written to the Auth outbox by forgot-password (INV-08). It carries only the user id: the
/// reset token is generated when the email is sent and is never stored in the outbox (INV-28).
/// </summary>
/// <param name="Id">Event id (Guid v7).</param>
/// <param name="OccurredAt">When the reset was requested (UTC).</param>
/// <param name="UserId">The user who asked to reset the password.</param>
[IntegrationEvent("auth.password-reset-requested.v1")]
internal sealed record PasswordResetRequested(Guid Id, DateTimeOffset OccurredAt, Guid UserId) : IIntegrationEvent
{
    public static PasswordResetRequested For(Guid userId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var now = timeProvider.GetUtcNow();
        return new PasswordResetRequested(Guid.CreateVersion7(now), now, userId);
    }
}
