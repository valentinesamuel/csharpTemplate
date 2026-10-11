using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.LockUser;

/// <summary>
/// Locks an account until an administrator unlocks it (<c>lockout_end = DateTimeOffset.MaxValue</c>), revokes every
/// session (reason <c>user_locked</c>) and updates the security stamp, in one transaction. Afterwards sign-in and
/// refresh fail with 401, and <c>live-session</c> endpoints reject the user's outstanding access tokens; other
/// endpoints accept them until they expire (at most 15 minutes, D-29). Idempotent. 404 <c>user_not_found</c>.
/// </summary>
internal sealed partial class LockUserUseCase(
    UserManager<AuthUser> userManager,
    RefreshTokenService refreshTokens,
    ILogger<LockUserUseCase> logger) : IUseCase<LockUserCommand, Done>
{
    public const string UserLockedEventType = "UserLocked";

    public async Task<Result<Done>> ExecuteAsync(LockUserCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await userManager.FindByIdAsync(input.UserId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return RefreshTokenService.UserNotFound;
        }

        // Identity refuses a lockout end for a user with lockout disabled.
        if (!user.LockoutEnabled && !(await userManager.SetLockoutEnabledAsync(user, enabled: true).ConfigureAwait(false)).Succeeded)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        if (!(await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue).ConfigureAwait(false)).Succeeded)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        var revoked = await refreshTokens
            .RevokeAllSessionsAsync(user.Id, SessionRevocationReasons.UserLocked, cancellationToken)
            .ConfigureAwait(false);
        if (revoked.IsFailure)
        {
            return revoked.Error;
        }

        if (!(await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false)).Succeeded)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        LogUserLocked(logger, UserLockedEventType, input.ActorId, user.Id, revoked.Value);
        return Done.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {ActorId} locked user {UserId} ({SessionCount} sessions revoked)")]
    private static partial void LogUserLocked(ILogger logger, string eventType, Guid actorId, Guid userId, int sessionCount);
}
