using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.MfaRecoveryCodes;

/// <summary>
/// Checks the password (<c>CheckPasswordSignInAsync(lockoutOnFailure: true)</c>; a wrong one, or a locked account, is
/// 400 <c>invalid_password</c>), then replaces the recovery codes with <see cref="RecoveryCodesResponse.Count"/> new
/// ones, stored hashed (D-15) and returned once. The old codes stop working. 409 <c>mfa_not_enabled</c> when two-factor
/// authentication is off.
/// </summary>
internal sealed partial class MfaRecoveryCodesUseCase(
    UserManager<AuthUser> userManager,
    SignInManager<AuthUser> signInManager,
    ILogger<MfaRecoveryCodesUseCase> logger) : IUseCase<MfaRecoveryCodesCommand, RecoveryCodesResponse>
{
    public const string RecoveryCodesRegeneratedEventType = "RecoveryCodesRegenerated";

    public async Task<Result<RecoveryCodesResponse>> ExecuteAsync(MfaRecoveryCodesCommand input, CancellationToken cancellationToken)
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

        if (!user.TwoFactorEnabled)
        {
            return AccountErrors.MfaNotEnabled;
        }

        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodesResponse.Count).ConfigureAwait(false);
        if (codes is null)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        LogRegenerated(logger, RecoveryCodesRegeneratedEventType, user.Id);
        return new RecoveryCodesResponse([.. codes]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} replaced the recovery codes")]
    private static partial void LogRegenerated(ILogger logger, string eventType, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovery code regeneration for user {UserId} rejected ({Reason})")]
    private static partial void LogRejected(ILogger logger, Guid userId, string reason);
}
