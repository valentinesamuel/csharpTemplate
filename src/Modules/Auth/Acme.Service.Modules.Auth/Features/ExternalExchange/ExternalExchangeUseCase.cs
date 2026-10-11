using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.External;
using Acme.Service.Modules.Auth.Features.Login;
using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Mfa;
using Acme.Service.Modules.Auth.Options;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Features.ExternalExchange;

/// <summary>
/// Exchanges a one-time external login code (D-13):
/// <list type="number">
/// <item>The code is consumed by one conditional update (<c>consumed_at IS NULL AND expires_at &gt; now</c>) on its
/// SHA-256 hash: single-use even under concurrency, dead after 60 seconds. A malformed, unknown, used or expired code is
/// 401 <c>invalid_exchange_code</c>.</item>
/// <item>A user locked since the callback is 401 <c>invalid_credentials</c>; an unconfirmed email (when
/// <c>Auth:Identity:RequireConfirmedEmail</c>) is 403 <c>email_not_verified</c>, as for a password sign-in.</item>
/// <item>A user with two-factor authentication gets <c>{mfaRequired:true, mfaToken}</c>, completed by
/// <c>POST /api/v1/auth/login/mfa</c>.</item>
/// <item>Otherwise a new session with <c>amr=[ext]</c> and its first token pair.</item>
/// </list>
/// Logged as <c>SecurityEvent</c>s with ids only; never the code or a token (INV-28).
/// </summary>
internal sealed partial class ExternalExchangeUseCase(
    AuthDbContext context,
    UserManager<AuthUser> userManager,
    RefreshTokenService refreshTokens,
    MfaTokenIssuer mfaTokens,
    IOptions<AuthIdentityOptions> identityOptions,
    TimeProvider timeProvider,
    ILogger<ExternalExchangeUseCase> logger) : IUseCase<ExternalExchangeCommand, LoginResponse>
{
    public const string ExchangeSucceededEventType = "ExternalCodeExchanged";

    public const string ExchangeFailedEventType = "ExternalCodeExchangeFailed";

    /// <summary>RFC 8176-style <c>amr</c> of a sign-in through the external provider.</summary>
    private static readonly string[] _externalAmr = ["ext"];

    public async Task<Result<LoginResponse>> ExecuteAsync(ExternalExchangeCommand input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!ExternalLoginCode.TryHash(input.Code, out var hash))
        {
            LogExchangeFailed(logger, ExchangeFailedEventType, null, "malformed");
            return ExternalErrors.InvalidExchangeCode;
        }

        var now = timeProvider.GetUtcNow();
        var consumed = await context.ExternalLoginCodes
            .Where(c => c.CodeHash == hash && c.ConsumedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (consumed != 1)
        {
            LogExchangeFailed(logger, ExchangeFailedEventType, null, "unknown_used_or_expired");
            return ExternalErrors.InvalidExchangeCode;
        }

        var userId = await context.ExternalLoginCodes.AsNoTracking()
            .Where(c => c.CodeHash == hash)
            .Select(c => c.UserId)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        var user = await userManager.FindByIdAsync(userId.ToString()).ConfigureAwait(false);
        if (user is null)
        {
            LogExchangeFailed(logger, ExchangeFailedEventType, userId, "unknown_user");
            return ExternalErrors.InvalidExchangeCode;
        }

        if (await userManager.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            LogExchangeFailed(logger, ExchangeFailedEventType, user.Id, "locked_out");
            return AccountErrors.InvalidCredentials;
        }

        if (identityOptions.Value.RequireConfirmedEmail && !user.EmailConfirmed)
        {
            LogExchangeFailed(logger, ExchangeFailedEventType, user.Id, "email_not_verified");
            return AccountErrors.EmailNotVerified;
        }

        if (user.TwoFactorEnabled)
        {
            var mfa = await mfaTokens.IssueAsync(user.Id, cancellationToken).ConfigureAwait(false);
            if (mfa.IsFailure)
            {
                return mfa.Error;
            }

            LogExchanged(logger, ExchangeSucceededEventType, user.Id, "mfa_required");
            return LoginResponse.MfaRequired(MfaRequiredResponse.From(mfa.Value));
        }

        var session = await refreshTokens.CreateSessionAsync(user.Id, _externalAmr, input.Client, cancellationToken).ConfigureAwait(false);
        if (session.IsFailure)
        {
            return session.Error.Type == ErrorType.NotFound ? ExternalErrors.InvalidExchangeCode : session.Error;
        }

        LogExchanged(logger, ExchangeSucceededEventType, user.Id, "signed_in");
        return LoginResponse.Complete(TokenResponse.From(session.Value));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SecurityEvent {EventType}: user {UserId} exchanged an external login code ({Result})")]
    private static partial void LogExchanged(ILogger logger, string eventType, Guid userId, string result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SecurityEvent {EventType}: external login code exchange failed for user {UserId} ({Reason})")]
    private static partial void LogExchangeFailed(ILogger logger, string eventType, Guid? userId, string reason);
}
