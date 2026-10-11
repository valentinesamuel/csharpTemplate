using System.Buffers.Text;
using System.Security.Cryptography;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.Modules.Auth.Options;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Sessions;

/// <summary>Client details recorded with a refresh token. Both are optional and informational only.</summary>
internal sealed record ClientInfo(string? IpAddress, string? UserAgent)
{
    public static readonly ClientInfo None = new(null, null);
}

/// <summary>An access token and the refresh token that renews it.</summary>
internal sealed record TokenPair(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid SessionId);

/// <summary>
/// Sessions and refresh tokens (D-11, INV-15). Creates a session with its first token pair (sign-in, P13), rotates
/// refresh tokens (strictly single-use; reuse revokes the whole session) and revokes sessions. Every operation runs
/// in one Auth unit of work; a database outage surfaces as <see cref="ErrorType.Unavailable"/> (503), never retried
/// (D-28). Tokens are never logged or stored in plaintext (INV-28).
/// </summary>
internal sealed partial class RefreshTokenService(
    AuthDbContext context,
    [FromKeyedServices(AuthDbContext.SchemaName)] IUnitOfWork unitOfWork,
    AccessTokenIssuer accessTokenIssuer,
    IOptions<JwtOptions> jwtOptions,
    IOptions<AuthIdentityOptions> identityOptions,
    TimeProvider timeProvider,
    ILogger<RefreshTokenService> logger)
{
    /// <summary>Number of random bytes in a refresh token.</summary>
    public const int TokenBytes = 32;

    /// <summary><c>EventType</c> of the security event logged when a used refresh token is presented again.</summary>
    public const string RefreshTokenReuseEventType = "RefreshTokenReuse";

    /// <summary>The only failure a client sees for an unknown, used, expired or revoked refresh token (401).</summary>
    public static readonly Error InvalidRefreshToken = Error.Unauthorized(
        "invalid_refresh_token", "The refresh token is invalid or expired.");

    /// <summary>The user of a new session does not exist.</summary>
    public static readonly Error UserNotFound = Error.NotFound("user_not_found", "The user does not exist.");

    /// <summary>
    /// Opens a new session for <paramref name="userId"/> and issues its first token pair (sign-in). The caller has
    /// already authenticated the user and checked lockout and email confirmation. Joins an open Auth transaction.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="authenticationMethods">RFC 8176 <c>amr</c> values, for example <c>["pwd"]</c>.</param>
    /// <param name="client">Client details recorded with the refresh token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Result<TokenPair>> CreateSessionAsync(
        Guid userId,
        IReadOnlyCollection<string> authenticationMethods,
        ClientInfo client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationMethods);
        ArgumentNullException.ThrowIfNull(client);

        TokenPair? pair = null;
        var result = await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                var user = await context.Users.AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.Id, u.Email })
                    .SingleOrDefaultAsync(token)
                    .ConfigureAwait(false);
                if (user is null)
                {
                    return Result.Failure(UserNotFound);
                }

                var options = jwtOptions.Value;
                var now = timeProvider.GetUtcNow();
                var session = new Session(user.Id, now, now + options.SessionAbsoluteLifetime, authenticationMethods);
                context.Sessions.Add(session);

                var (refreshToken, stored) = NewRefreshToken(session, now, client);
                context.RefreshTokens.Add(stored);

                var access = await IssueAccessTokenAsync(user.Id, user.Email, session, token).ConfigureAwait(false);
                pair = new TokenPair(access.Token, access.ExpiresAt, refreshToken, stored.ExpiresAt, session.Id);
                return Result.Success();
            },
            retryOnTransientFailure: false,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? pair! : Result.Failure<TokenPair>(result.Error);
    }

    /// <summary>
    /// Exchanges <paramref name="refreshToken"/> for a new token pair in the same session (plan P12, steps 1 to 6, in one
    /// transaction). An unknown, expired or revoked token fails with <see cref="InvalidRefreshToken"/>. A token that was
    /// already used (including a concurrent use of the same token) is reuse: the whole session is revoked
    /// (<c>reuse_detected</c>), a <c>RefreshTokenReuse</c> security event is logged, and the call fails with
    /// <see cref="InvalidRefreshToken"/>. A locked, deleted or (when required) unconfirmed user revokes the session.
    /// </summary>
    public async Task<Result<TokenPair>> RefreshAsync(string? refreshToken, ClientInfo client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!TryHash(refreshToken, out var hash))
        {
            return Result.Failure<TokenPair>(InvalidRefreshToken);
        }

        TokenPair? pair = null;
        var result = await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                pair = await RotateAsync(hash, client, token).ConfigureAwait(false);

                // Revocations made on the failure paths must commit, so the operation itself always succeeds.
                return Result.Success();
            },
            retryOnTransientFailure: false,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return Result.Failure<TokenPair>(result.Error);
        }

        return pair is null ? Result.Failure<TokenPair>(InvalidRefreshToken) : pair;
    }

    /// <summary>
    /// Revokes session <paramref name="sessionId"/> when it belongs to <paramref name="userId"/>. Returns true when a
    /// session was revoked by this call, false when it does not exist, belongs to someone else or was already revoked.
    /// </summary>
    public async Task<Result<bool>> RevokeSessionAsync(Guid sessionId, Guid userId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var revoked = 0;
        var result = await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                revoked = await RevokeWhereAsync(s => s.Id == sessionId && s.UserId == userId, reason, token).ConfigureAwait(false);
                return Result.Success();
            },
            retryOnTransientFailure: false,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? revoked == 1 : Result.Failure<bool>(result.Error);
    }

    /// <summary>
    /// Revokes the session that <paramref name="refreshToken"/> (used or not) belongs to, when that session belongs to
    /// <paramref name="userId"/> (logout). Returns false for a malformed or unknown token, another user's session or an
    /// already revoked one, without revealing which.
    /// </summary>
    public async Task<Result<bool>> RevokeSessionByRefreshTokenAsync(string? refreshToken, Guid userId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!TryHash(refreshToken, out var hash))
        {
            return false;
        }

        var revoked = 0;
        var result = await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                var sessionId = await context.RefreshTokens.AsNoTracking()
                    .Where(t => t.TokenHash == hash)
                    .Select(t => (Guid?)t.SessionId)
                    .SingleOrDefaultAsync(token)
                    .ConfigureAwait(false);
                if (sessionId is { } id)
                {
                    revoked = await RevokeWhereAsync(s => s.Id == id && s.UserId == userId, reason, token).ConfigureAwait(false);
                }

                return Result.Success();
            },
            retryOnTransientFailure: false,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? revoked == 1 : Result.Failure<bool>(result.Error);
    }

    /// <summary>Revokes every active session of <paramref name="userId"/> and returns how many were revoked.</summary>
    public async Task<Result<int>> RevokeAllSessionsAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var revoked = 0;
        var result = await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                revoked = await RevokeWhereAsync(s => s.UserId == userId, reason, token).ConfigureAwait(false);
                return Result.Success();
            },
            retryOnTransientFailure: false,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? revoked : Result.Failure<int>(result.Error);
    }

    /// <summary>
    /// Revokes every active session of <paramref name="userId"/> except <paramref name="keepSessionId"/> and returns how
    /// many were revoked. Joins an open Auth transaction.
    /// </summary>
    public async Task<Result<int>> RevokeOtherSessionsAsync(Guid userId, Guid keepSessionId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var revoked = 0;
        var result = await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                revoked = await RevokeWhereAsync(s => s.UserId == userId && s.Id != keepSessionId, reason, token).ConfigureAwait(false);
                return Result.Success();
            },
            retryOnTransientFailure: false,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? revoked : Result.Failure<int>(result.Error);
    }

    /// <summary>SHA-256 of the decoded token; false when the value is not a well-formed refresh token.</summary>
    internal static bool TryHash(string? refreshToken, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrEmpty(refreshToken)
            || refreshToken.Length != Base64Url.GetEncodedLength(TokenBytes)
            || !refreshToken.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[TokenBytes];
        if (!Base64Url.TryDecodeFromChars(refreshToken, bytes, out var written) || written != TokenBytes)
        {
            return false;
        }

        hash = SHA256.HashData(bytes);
        return true;
    }

    private async Task<TokenPair?> RotateAsync(byte[] hash, ClientInfo client, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // 1. Look up by hash.
        var stored = await context.RefreshTokens
            .SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        var session = await context.Sessions
            .SingleOrDefaultAsync(s => s.Id == stored.SessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        // 2. A used token is reuse; a revoked or expired session or token is invalid.
        if (stored.UsedAt is not null)
        {
            RevokeForReuse(session, now);
            return null;
        }

        if (!session.IsActive(now) || stored.ExpiresAt <= now)
        {
            return null;
        }

        // 3. The user must still be allowed to sign in.
        var user = await context.Users.AsNoTracking()
            .Where(u => u.Id == session.UserId)
            .Select(u => new { u.Id, u.Email, u.EmailConfirmed, u.LockoutEnd })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (user is null
            || user.LockoutEnd > now
            || (identityOptions.Value.RequireConfirmedEmail && !user.EmailConfirmed))
        {
            if (session.Revoke(now, SessionRevocationReasons.UserInactive))
            {
                LogSessionRevokedInactiveUser(logger, session.Id, session.UserId);
            }

            return null;
        }

        // 4. Claim the token atomically; a concurrent exchange of the same token loses here and counts as reuse.
        var claimed = await context.RefreshTokens
            .Where(t => t.Id == stored.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (claimed != 1)
        {
            RevokeForReuse(session, now);
            return null;
        }

        // 5. The replacement, in the same session, never outliving it.
        var (refreshToken, next) = NewRefreshToken(session, now, client);
        context.RefreshTokens.Add(next);
        stored.ReplacedById = next.Id;

        // 6. A fresh access token with the user's current permissions.
        var access = await IssueAccessTokenAsync(user.Id, user.Email, session, cancellationToken).ConfigureAwait(false);
        return new TokenPair(access.Token, access.ExpiresAt, refreshToken, next.ExpiresAt, session.Id);
    }

    private void RevokeForReuse(Session session, DateTimeOffset now)
    {
        session.Revoke(now, SessionRevocationReasons.ReuseDetected);
        LogRefreshTokenReuse(logger, RefreshTokenReuseEventType, session.Id, session.UserId);
    }

    private (string Token, RefreshToken Stored) NewRefreshToken(Session session, DateTimeOffset now, ClientInfo client)
    {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        var token = Base64Url.EncodeToString(bytes);

        var lifetimeEnd = now + jwtOptions.Value.RefreshTokenLifetime;
        var expiresAt = lifetimeEnd < session.AbsoluteExpiresAt ? lifetimeEnd : session.AbsoluteExpiresAt;
        return (token, new RefreshToken(session.Id, SHA256.HashData(bytes), now, expiresAt, client.IpAddress, client.UserAgent));
    }

    private async Task<AccessToken> IssueAccessTokenAsync(Guid userId, string? email, Session session, CancellationToken cancellationToken)
    {
        var roles = await (
                from userRole in context.UserRoles
                join role in context.Roles on userRole.RoleId equals role.Id
                where userRole.UserId == userId && role.Name != null
                orderby role.Name
                select role.Name!)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var permissions = await (
                from userRole in context.UserRoles
                join grant in context.RolePermissions on userRole.RoleId equals grant.RoleId
                where userRole.UserId == userId
                select grant.PermissionName)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return accessTokenIssuer.Issue(new AccessTokenSubject(userId, email, session.Id, roles, permissions, session.Amr));
    }

    private async Task<int> RevokeWhereAsync(
        System.Linq.Expressions.Expression<Func<Session, bool>> predicate,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await context.Sessions
            .Where(predicate)
            .Where(s => s.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedReason, reason),
                cancellationToken)
            .ConfigureAwait(false);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SecurityEvent {EventType}: a used refresh token was presented again; session {SessionId} of user {UserId} is revoked")]
    private static partial void LogRefreshTokenReuse(ILogger logger, string eventType, Guid sessionId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} of user {UserId} revoked on refresh: the user can no longer sign in")]
    private static partial void LogSessionRevokedInactiveUser(ILogger logger, Guid sessionId, Guid userId);
}
