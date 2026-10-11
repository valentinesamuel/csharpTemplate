using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Mfa;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.MfaDisable;

/// <summary>
/// Disables two-factor authentication:
/// <list type="number">
/// <item>The password is checked with <c>CheckPasswordSignInAsync(lockoutOnFailure: true)</c>: a wrong one, or a locked
/// account, is 400 <c>invalid_password</c> and counts towards lockout.</item>
/// <item>In one transaction: two-factor sign-in off, a new (unused) authenticator key replacing the old one (the
/// security stamp changes), the recovery codes discarded and the last accepted time step forgotten.</item>
/// </list>
/// Idempotent: disabling an account without two-factor authentication succeeds too.
/// </summary>
internal sealed partial class MfaDisableUseCase(
    UserManager<AuthUser> userManager,
    SignInManager<AuthUser> signInManager,
    TotpReplayGuard totp,
    [FromKeyedServices(AuthDbContext.SchemaName)] IUnitOfWork unitOfWork,
    ILogger<MfaDisableUseCase> logger) : IUseCase<MfaDisableCommand, Done>
{
    public const string MfaDisabledEventType = "MfaDisabled";

    public async Task<Result<Done>> ExecuteAsync(MfaDisableCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await userManager.FindByIdAsync(input.UserId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return RefreshTokenService.UserNotFound;
        }

        var check = await signInManager.CheckPasswordSignInAsync(user, input.Password, lockoutOnFailure: true).ConfigureAwait(false);
        if (!check.Succeeded)
        {
            LogRejected(logger, user.Id, check.IsLockedOut ? "locked_out" : "wrong_password");
            return AccountErrors.IncorrectPassword("password");
        }

        var result = await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                if (!(await userManager.SetTwoFactorEnabledAsync(user, false).ConfigureAwait(false)).Succeeded
                    || !(await userManager.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false)).Succeeded
                    || await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 0).ConfigureAwait(false) is null)
                {
                    return Result.Failure(AccountErrors.ConcurrencyConflict);
                }

                await totp.ResetAsync(user.Id, token).ConfigureAwait(false);
                return Result.Success();
            },
            retryOnTransientFailure: false,
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error;
        }

        LogMfaDisabled(logger, MfaDisabledEventType, user.Id);
        return Done.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} disabled two-factor authentication")]
    private static partial void LogMfaDisabled(ILogger logger, string eventType, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disabling two-factor authentication for user {UserId} rejected ({Reason})")]
    private static partial void LogRejected(ILogger logger, Guid userId, string reason);
}
