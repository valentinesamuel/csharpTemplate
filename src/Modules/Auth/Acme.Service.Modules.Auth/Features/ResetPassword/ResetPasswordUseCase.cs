using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.ResetPassword;

/// <summary>
/// Sets a new password with Identity's password reset token (lifespan <c>Auth:Identity:TokenLifespan</c>, bound to the
/// security stamp), in the command's transaction:
/// <list type="bullet">
/// <item>An unknown user and a bad, expired or used token fail alike with 400 <c>invalid_token</c>; a new password the
/// policy rejects is 400 <c>validation_failed</c>.</item>
/// <item>On success the security stamp changes (so the link works once), every session is revoked
/// (<c>password_reset</c>), and the failed-attempt counter and any lockout are cleared: the user proved control of the
/// address.</item>
/// </list>
/// </summary>
internal sealed partial class ResetPasswordUseCase(
    UserManager<AuthUser> userManager,
    RefreshTokenService refreshTokens,
    ILogger<ResetPasswordUseCase> logger) : IUseCase<ResetPasswordCommand, Done>
{
    public const string PasswordResetEventType = "PasswordReset";

    public async Task<Result<Done>> ExecuteAsync(ResetPasswordCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await userManager.FindByIdAsync(input.UserId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return AccountErrors.InvalidToken;
        }

        var reset = await userManager.ResetPasswordAsync(user, input.Token, input.NewPassword).ConfigureAwait(false);
        if (!reset.Succeeded)
        {
            if (reset.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
            {
                LogResetRejected(logger, user.Id);
                return AccountErrors.InvalidToken;
            }

            return reset.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure))
                ? AccountErrors.ConcurrencyConflict
                : AccountErrors.PasswordRejected(reset, "newPassword");
        }

        var unlocked = await userManager.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
        if (unlocked.Succeeded && await userManager.GetLockoutEndDateAsync(user).ConfigureAwait(false) is not null)
        {
            unlocked = await userManager.SetLockoutEndDateAsync(user, null).ConfigureAwait(false);
        }

        if (!unlocked.Succeeded)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        var revoked = await refreshTokens
            .RevokeAllSessionsAsync(user.Id, SessionRevocationReasons.PasswordReset, cancellationToken)
            .ConfigureAwait(false);
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        LogPasswordReset(logger, PasswordResetEventType, user.Id, revoked.Value);
        return Done.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} reset the password; {SessionCount} sessions revoked")]
    private static partial void LogPasswordReset(ILogger logger, string eventType, Guid userId, int sessionCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset for user {UserId} rejected: invalid or expired token")]
    private static partial void LogResetRejected(ILogger logger, Guid userId);
}
