using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Mfa;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.MfaVerify;

/// <summary>
/// Verifies a code of the key from <c>POST /mfa/authenticator</c> (<see cref="TotpReplayGuard"/>, so the step is used
/// up), then, in the command's transaction, enables two-factor authentication and replaces the recovery codes with
/// <see cref="RecoveryCodesResponse.Count"/> new ones, stored hashed (D-15) and returned once.
/// </summary>
internal sealed partial class MfaVerifyUseCase(
    UserManager<AuthUser> userManager,
    TotpReplayGuard totp,
    ILogger<MfaVerifyUseCase> logger) : IUseCase<MfaVerifyCommand, RecoveryCodesResponse>
{
    public const string MfaEnabledEventType = "MfaEnabled";

    public async Task<Result<RecoveryCodesResponse>> ExecuteAsync(MfaVerifyCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await userManager.FindByIdAsync(input.UserId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            return RefreshTokenService.UserNotFound;
        }

        if (user.TwoFactorEnabled)
        {
            return AccountErrors.MfaAlreadyEnabled;
        }

        if (!await totp.TryAcceptAsync(user, input.Code, cancellationToken).ConfigureAwait(false))
        {
            LogVerificationRejected(logger, user.Id);
            return AccountErrors.InvalidVerificationCode;
        }

        var enabled = await userManager.SetTwoFactorEnabledAsync(user, true).ConfigureAwait(false);
        if (!enabled.Succeeded)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodesResponse.Count).ConfigureAwait(false);
        if (codes is null)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        LogMfaEnabled(logger, MfaEnabledEventType, user.Id);
        return new RecoveryCodesResponse([.. codes]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} enabled two-factor authentication")]
    private static partial void LogMfaEnabled(ILogger logger, string eventType, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Authenticator verification for user {UserId} rejected: wrong, reused or expired code, or no key")]
    private static partial void LogVerificationRejected(ILogger logger, Guid userId);
}
