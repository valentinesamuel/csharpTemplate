using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Mfa;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Features.LoginMfa;

/// <summary>
/// The second step of a two-factor sign-in (D-15, WH-11):
/// <list type="number">
/// <item>The mfa token must validate (its own validation parameters) and its challenge must be open: not consumed,
/// fewer than <see cref="MfaChallenge.MaxAttempts"/> wrong codes, not expired. Otherwise 401 <c>invalid_mfa_token</c>.</item>
/// <item>A locked account fails with 401 <c>invalid_mfa_code</c> without checking the code.</item>
/// <item>The authenticator code is verified by <see cref="TotpReplayGuard"/> (a time step is accepted once), or the
/// recovery code is redeemed (once).</item>
/// <item>A wrong code increments the challenge's attempts and Identity's failed-attempt counter (lockout); 401
/// <c>invalid_mfa_code</c>.</item>
/// <item>A valid code consumes the challenge atomically, resets the failed-attempt counter and opens a session with
/// <c>amr=[pwd,mfa]</c>.</item>
/// </list>
/// Each write commits on its own (see <see cref="LoginMfaCommand"/>). Outcomes are logged as <c>SecurityEvent</c>s with
/// ids only; never the token or a code (INV-28).
/// </summary>
internal sealed partial class LoginMfaUseCase(
    AuthDbContext context,
    UserManager<AuthUser> userManager,
    MfaTokenIssuer mfaTokens,
    TotpReplayGuard totp,
    RefreshTokenService refreshTokens,
    TimeProvider timeProvider,
    ILogger<LoginMfaUseCase> logger) : IUseCase<LoginMfaCommand, TokenResponse>
{
    public const string MfaSucceededEventType = "MfaSucceeded";

    public const string MfaFailedEventType = "MfaFailed";

    /// <summary>RFC 8176 <c>amr</c> of a sign-in with a password and a second factor.</summary>
    private static readonly string[] _mfaAmr = ["pwd", "mfa"];

    public async Task<Result<TokenResponse>> ExecuteAsync(LoginMfaCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var subject = await mfaTokens.ValidateAsync(input.MfaToken).ConfigureAwait(false);
        if (subject is null)
        {
            LogMfaFailed(logger, MfaFailedEventType, null, "invalid_token");
            return AccountErrors.InvalidMfaToken;
        }

        var now = timeProvider.GetUtcNow();
        var challenge = await context.MfaChallenges.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Jti == subject.ChallengeId && c.UserId == subject.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (challenge is null || !challenge.IsOpen(now))
        {
            LogMfaFailed(logger, MfaFailedEventType, subject.UserId, "challenge_closed");
            return AccountErrors.InvalidMfaToken;
        }

        var user = await userManager.FindByIdAsync(subject.UserId.ToString()).ConfigureAwait(false);
        if (user is null || !user.TwoFactorEnabled)
        {
            LogMfaFailed(logger, MfaFailedEventType, subject.UserId, "mfa_not_enabled");
            return AccountErrors.InvalidMfaToken;
        }

        if (await userManager.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            LogMfaFailed(logger, MfaFailedEventType, user.Id, "locked_out");
            return AccountErrors.InvalidMfaCode;
        }

        var verified = input.RecoveryCode is { Length: > 0 } recoveryCode
            ? (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode.Trim()).ConfigureAwait(false)).Succeeded
            : await totp.TryAcceptAsync(user, input.Code, cancellationToken).ConfigureAwait(false);
        var method = input.RecoveryCode is { Length: > 0 } ? "recovery_code" : "authenticator";
        if (!verified)
        {
            await RecordFailedAttemptAsync(challenge.Jti, cancellationToken).ConfigureAwait(false);
            var failed = await userManager.AccessFailedAsync(user).ConfigureAwait(false);
            var lockedNow = failed.Succeeded && await userManager.IsLockedOutAsync(user).ConfigureAwait(false);
            LogMfaFailed(logger, MfaFailedEventType, user.Id, lockedNow ? $"wrong_{method}_locked_out" : $"wrong_{method}");
            return AccountErrors.InvalidMfaCode;
        }

        // Consume the challenge exactly once; a concurrent completion of the same challenge loses here.
        var consumed = await context.MfaChallenges
            .Where(c => c.Jti == challenge.Jti && c.ConsumedAt == null && c.Attempts < MfaChallenge.MaxAttempts && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (consumed != 1)
        {
            LogMfaFailed(logger, MfaFailedEventType, user.Id, "challenge_closed");
            return AccountErrors.InvalidMfaToken;
        }

        var reset = await userManager.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
        if (!reset.Succeeded)
        {
            return AccountErrors.ConcurrencyConflict;
        }

        var session = await refreshTokens.CreateSessionAsync(user.Id, _mfaAmr, input.Client, cancellationToken).ConfigureAwait(false);
        if (session.IsFailure)
        {
            return session.Error.Type == ErrorType.NotFound ? AccountErrors.InvalidMfaToken : session.Error;
        }

        LogMfaSucceeded(logger, MfaSucceededEventType, user.Id, method, session.Value.SessionId);
        return TokenResponse.From(session.Value);
    }

    private Task<int> RecordFailedAttemptAsync(Guid challengeId, CancellationToken cancellationToken) =>
        context.MfaChallenges
            .Where(c => c.Jti == challengeId && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => c.Attempts + 1), cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} completed two-factor sign-in with {Method}; session {SessionId}")]
    private static partial void LogMfaSucceeded(ILogger logger, string eventType, Guid userId, string method, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SecurityEvent {EventType}: two-factor sign-in failed for user {UserId} ({Reason})")]
    private static partial void LogMfaFailed(ILogger logger, string eventType, Guid? userId, string reason);
}
