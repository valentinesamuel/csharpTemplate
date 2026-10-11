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

namespace Acme.Service.Modules.Auth.Features.Login;

/// <summary>
/// Password sign-in (D-23):
/// <list type="number">
/// <item>Unknown email: a dummy password hash equalises timing; 401 <c>invalid_credentials</c>.</item>
/// <item>Locked account: the same dummy hash; 401 <c>invalid_credentials</c> (even with the correct password).</item>
/// <item><c>CheckPasswordSignInAsync(lockoutOnFailure: true)</c>: a wrong password counts towards lockout; 401
/// <c>invalid_credentials</c>.</item>
/// <item>Correct password but unconfirmed email (when <c>Auth:Identity:RequireConfirmedEmail</c>): 403
/// <c>email_not_verified</c>, checked only after the password so it never reveals an account.</item>
/// <item>Two-factor authentication enabled: no tokens; a new <see cref="MfaChallenge"/> and its mfa token
/// (<c>{mfaRequired:true, mfaToken}</c>), completed by <c>POST /api/v1/auth/login/mfa</c>. Identity does not reset the
/// failed-attempt counter here, so wrong codes keep counting towards lockout.</item>
/// <item>Otherwise a new session with <c>amr=[pwd]</c> and its first token pair.</item>
/// </list>
/// Every outcome is logged as a <c>SecurityEvent</c> with the user id when known; never the email, the password or a
/// token.
/// </summary>
internal sealed partial class LoginUseCase(
    UserManager<AuthUser> userManager,
    SignInManager<AuthUser> signInManager,
    RefreshTokenService refreshTokens,
    MfaTokenIssuer mfaTokens,
    PasswordTimingEqualizer timingEqualizer,
    IOptions<AuthIdentityOptions> identityOptions,
    ILogger<LoginUseCase> logger) : IUseCase<LoginCommand, LoginResponse>
{
    public const string LoginSucceededEventType = "LoginSucceeded";

    public const string LoginFailedEventType = "LoginFailed";

    public const string MfaChallengeIssuedEventType = "MfaChallengeIssued";

    /// <summary>RFC 8176 <c>amr</c> of a password sign-in.</summary>
    private static readonly string[] _passwordAmr = ["pwd"];

    public async Task<Result<LoginResponse>> ExecuteAsync(LoginCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var user = await userManager.FindByEmailAsync(input.Email.Trim()).ConfigureAwait(false);
        if (user is null)
        {
            timingEqualizer.VerifyAgainstDummy(input.Password);
            LogLoginFailed(logger, LoginFailedEventType, null, "unknown_user");
            return AccountErrors.InvalidCredentials;
        }

        if (await userManager.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            timingEqualizer.VerifyAgainstDummy(input.Password);
            LogLoginFailed(logger, LoginFailedEventType, user.Id, "locked_out");
            return AccountErrors.InvalidCredentials;
        }

        var check = await signInManager.CheckPasswordSignInAsync(user, input.Password, lockoutOnFailure: true).ConfigureAwait(false);
        if (!check.Succeeded)
        {
            LogLoginFailed(logger, LoginFailedEventType, user.Id, check.IsLockedOut ? "locked_out_now" : "bad_password");
            return AccountErrors.InvalidCredentials;
        }

        if (identityOptions.Value.RequireConfirmedEmail && !user.EmailConfirmed)
        {
            LogLoginFailed(logger, LoginFailedEventType, user.Id, "email_not_verified");
            return AccountErrors.EmailNotVerified;
        }

        if (user.TwoFactorEnabled)
        {
            // Never password-only tokens for an MFA account: the second step issues them.
            var mfa = await mfaTokens.IssueAsync(user.Id, cancellationToken).ConfigureAwait(false);
            if (mfa.IsFailure)
            {
                return mfa.Error;
            }

            LogMfaChallengeIssued(logger, MfaChallengeIssuedEventType, user.Id);
            return LoginResponse.MfaRequired(MfaRequiredResponse.From(mfa.Value));
        }

        var session = await refreshTokens.CreateSessionAsync(user.Id, _passwordAmr, input.Client, cancellationToken).ConfigureAwait(false);
        if (session.IsFailure)
        {
            // A user deleted in between is just another failed sign-in; an outage stays 503.
            return session.Error.Type == ErrorType.NotFound ? AccountErrors.InvalidCredentials : session.Error;
        }

        LogLoginSucceeded(logger, LoginSucceededEventType, user.Id, session.Value.SessionId);
        return LoginResponse.Complete(TokenResponse.From(session.Value));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} signed in with a password; session {SessionId}")]
    private static partial void LogLoginSucceeded(ILogger logger, string eventType, Guid userId, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} entered the correct password; a second factor is required")]
    private static partial void LogMfaChallengeIssued(ILogger logger, string eventType, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SecurityEvent {EventType}: sign-in failed for user {UserId} ({Reason})")]
    private static partial void LogLoginFailed(ILogger logger, string eventType, Guid? userId, string reason);
}
