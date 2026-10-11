using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Notifications;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Notifications;

/// <summary>Payload of <see cref="SendEmailVerificationJob"/>: only the user id, never a token (INV-28).</summary>
/// <param name="UserId">The user to email.</param>
internal sealed record SendEmailVerificationPayload(Guid UserId);

/// <summary>
/// Sends the email confirmation link. The token is generated here, at send time, by Identity's email confirmation
/// token provider (lifespan <c>Auth:Identity:TokenLifespan</c>, bound to the security stamp), so it never exists in the
/// outbox or the job store (INV-28). The link is <c>Auth:Links:ConfirmEmailUrl?userId=…&amp;token=…</c>. Neither the
/// token nor the link is logged.
/// </summary>
/// <remarks>
/// Runs outside any transaction (INV-10). Idempotent in effect: a missing or already confirmed user is skipped, and a
/// repeated run only sends another valid link (duplicate delivery, P13 failure modes). A sender failure propagates so the
/// job provider retries; the user can also request a new email.
/// </remarks>
internal sealed partial class SendEmailVerificationJob(
    UserManager<AuthUser> userManager,
    INotificationSender notificationSender,
    IOptions<AuthLinksOptions> linkOptions,
    IOptions<AuthIdentityOptions> identityOptions,
    ILogger<SendEmailVerificationJob> logger) : IJob<SendEmailVerificationPayload>
{
    public async Task ExecuteAsync(SendEmailVerificationPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var user = await userManager.FindByIdAsync(payload.UserId.ToString()).ConfigureAwait(false);
        if (user?.Email is not { Length: > 0 } email)
        {
            LogSkipped(logger, payload.UserId, "the user does not exist");
            return;
        }

        if (user.EmailConfirmed)
        {
            LogSkipped(logger, payload.UserId, "the email address is already confirmed");
            return;
        }

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user).ConfigureAwait(false);
        var link = EmailTemplates.BuildLink(linkOptions.Value.ConfirmEmailUrl!, user.Id, token);
        var message = EmailTemplates.EmailVerification(email, link, identityOptions.Value.TokenLifespan);

        await notificationSender.SendEmailAsync(message, cancellationToken).ConfigureAwait(false);
        LogSent(logger, user.Id);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email verification for user {UserId} skipped: {Reason}")]
    private static partial void LogSkipped(ILogger logger, Guid userId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email verification sent to user {UserId}")]
    private static partial void LogSent(ILogger logger, Guid userId);
}
