using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.UnlockUser;

/// <summary>
/// Clears the lockout (an administrator lock or an Identity lockout after failed sign-ins) and resets the failed
/// sign-in counter. Revoked sessions stay revoked: the user signs in again. Idempotent. 404 <c>user_not_found</c>.
/// </summary>
internal sealed partial class UnlockUserUseCase(
    UserManager<AuthUser> userManager,
    ILogger<UnlockUserUseCase> logger) : IUseCase<UnlockUserCommand, Done>
{
    public const string UserUnlockedEventType = "UserUnlocked";

    public async Task<Result<Done>> ExecuteAsync(UnlockUserCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await userManager.FindByIdAsync(input.UserId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return RefreshTokenService.UserNotFound;
        }

        if (user.LockoutEnd is not null
            && !(await userManager.SetLockoutEndDateAsync(user, null).ConfigureAwait(false)).Succeeded)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        if (!(await userManager.ResetAccessFailedCountAsync(user).ConfigureAwait(false)).Succeeded)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        LogUserUnlocked(logger, UserUnlockedEventType, input.ActorId, user.Id);
        return Done.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {ActorId} unlocked user {UserId}")]
    private static partial void LogUserUnlocked(ILogger logger, string eventType, Guid actorId, Guid userId);
}
