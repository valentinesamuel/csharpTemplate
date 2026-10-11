using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.ChangePassword;

/// <summary>
/// Changes the password (D-29):
/// <list type="number">
/// <item>The current password is checked with <c>CheckPasswordSignInAsync(lockoutOnFailure: true)</c>: a wrong one, or a
/// locked account, is 400 <c>invalid_password</c> and counts towards lockout.</item>
/// <item>In one transaction: the new password is set (Identity's policy; the security stamp changes, which also
/// invalidates outstanding emailed tokens) and every session except the caller's is revoked
/// (<c>password_changed</c>).</item>
/// </list>
/// </summary>
internal sealed partial class ChangePasswordUseCase(
    UserManager<AuthUser> userManager,
    SignInManager<AuthUser> signInManager,
    RefreshTokenService refreshTokens,
    [FromKeyedServices(AuthDbContext.SchemaName)] IUnitOfWork unitOfWork,
    ILogger<ChangePasswordUseCase> logger) : IUseCase<ChangePasswordCommand, Done>
{
    public const string PasswordChangedEventType = "PasswordChanged";

    public async Task<Result<Done>> ExecuteAsync(ChangePasswordCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await userManager.FindByIdAsync(input.UserId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return RefreshTokenService.UserNotFound;
        }

        var check = await signInManager.CheckPasswordSignInAsync(user, input.CurrentPassword, lockoutOnFailure: true).ConfigureAwait(false);
        if (!check.Succeeded)
        {
            LogPasswordChangeRejected(logger, user.Id, check.IsLockedOut ? "locked_out" : "wrong_password");
            return AccountErrors.IncorrectPassword("currentPassword");
        }

        var revoked = 0;
        var result = await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                var changed = await userManager.ChangePasswordAsync(user, input.CurrentPassword, input.NewPassword).ConfigureAwait(false);
                if (!changed.Succeeded)
                {
                    return changed.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure))
                        ? Result.Failure(AccountErrors.ConcurrencyConflict)
                        : Result.Failure(AccountErrors.PasswordRejected(changed, "newPassword"));
                }

                var others = await refreshTokens
                    .RevokeOtherSessionsAsync(user.Id, input.SessionId, SessionRevocationReasons.PasswordChanged, token)
                    .ConfigureAwait(false);
                if (others.IsFailure)
                {
                    return Result.Failure(others.Error);
                }

                revoked = others.Value;
                return Result.Success();
            },
            retryOnTransientFailure: false,
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error;
        }

        LogPasswordChanged(logger, PasswordChangedEventType, user.Id, input.SessionId, revoked);
        return Done.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} changed the password in session {SessionId}; {SessionCount} other sessions revoked")]
    private static partial void LogPasswordChanged(ILogger logger, string eventType, Guid userId, Guid sessionId, int sessionCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Password change for user {UserId} rejected ({Reason})")]
    private static partial void LogPasswordChangeRejected(ILogger logger, Guid userId, string reason);
}
