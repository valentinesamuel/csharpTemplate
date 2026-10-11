using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Mfa;
using Acme.Service.Modules.Auth.Options;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Features.MfaSetup;

/// <summary>
/// Replaces the caller's authenticator key (stored encrypted, D-15; the security stamp changes) and returns it with its
/// <c>otpauth://</c> URI (Key URI format), without a QR image. Refused with 409 <c>mfa_already_enabled</c> while
/// two-factor authentication is on, so a stolen access token cannot swap the second factor; disabling needs the
/// password. The key is never logged (INV-28).
/// </summary>
internal sealed partial class MfaSetupUseCase(
    UserManager<AuthUser> userManager,
    TotpReplayGuard totp,
    IOptions<AuthMfaOptions> mfaOptions,
    ILogger<MfaSetupUseCase> logger) : IUseCase<MfaSetupCommand, MfaSetupResponse>
{
    public async Task<Result<MfaSetupResponse>> ExecuteAsync(MfaSetupCommand input, CancellationToken cancellationToken)
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

        var reset = await userManager.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        if (!reset.Succeeded)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        await totp.ResetAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var key = await userManager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The authenticator key was not stored.");

        LogAuthenticatorReset(logger, user.Id);
        return new MfaSetupResponse(key, AuthenticatorUri(mfaOptions.Value.Issuer!, user.Email ?? user.Id.ToString(), key));
    }

    /// <summary><c>otpauth://totp/{issuer}:{account}?secret={key}&amp;issuer={issuer}&amp;digits=6&amp;period=30</c>, URL-encoded.</summary>
    internal static string AuthenticatorUri(string issuer, string account, string key)
    {
        var encodedIssuer = Uri.EscapeDataString(issuer);
        return FormattableString.Invariant(
            $"otpauth://totp/{encodedIssuer}:{Uri.EscapeDataString(account)}?secret={key}&issuer={encodedIssuer}&digits={TotpReplayGuard.Digits}&period={(int)TotpReplayGuard.Period.TotalSeconds}");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} received a new authenticator key; two-factor authentication stays off until verified")]
    private static partial void LogAuthenticatorReset(ILogger logger, Guid userId);
}
