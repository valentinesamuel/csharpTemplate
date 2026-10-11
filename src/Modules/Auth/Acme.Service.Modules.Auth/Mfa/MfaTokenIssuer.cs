using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Results;
using Acme.Service.Modules.Auth.Persistence;
using Acme.Service.Modules.Auth.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Acme.Service.Modules.Auth.Mfa;

/// <summary>A signed mfa token and its expiry.</summary>
internal sealed record MfaToken(string Token, DateTimeOffset ExpiresAt)
{
    // Never print the token (INV-28).
    public override string ToString() => nameof(MfaToken);
}

/// <summary>What a valid mfa token identifies: the user and the challenge (<c>jti</c>).</summary>
internal sealed record MfaTokenSubject(Guid UserId, Guid ChallengeId);

/// <summary>
/// Issues and validates the mfa token that links the two steps of a sign-in with two-factor authentication: an RS256
/// JWT signed by the Active key of the ring, with header <c>typ=mfa+jwt</c>, audience <c>{Audience}:mfa</c>, claims
/// <c>iss, aud, sub, jti, iat, nbf, exp</c> and a lifetime of <see cref="Lifetime"/>. Its <c>jti</c> is recorded as an
/// <see cref="MfaChallenge"/>.
/// </summary>
/// <remarks>
/// The token can never authenticate an API call (INV-14): JwtBearer accepts only <c>typ=at+jwt</c> and the plain
/// audience. Conversely, <see cref="ValidateAsync"/> has its own validation parameters and accepts only mfa tokens.
/// </remarks>
internal sealed class MfaTokenIssuer
{
    /// <summary>The JOSE <c>typ</c> of mfa tokens.</summary>
    public const string TokenType = "mfa+jwt";

    /// <summary>Appended to <c>Auth:Jwt:Audience</c> to form the audience of mfa tokens.</summary>
    public const string AudienceSuffix = ":mfa";

    /// <summary>Longest token accepted for validation; longer input is rejected without parsing.</summary>
    public const int MaxTokenLength = 4096;

    /// <summary>Lifetime of an mfa token and its challenge.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    // Times come from the injected TimeProvider, never from the handler's clock.
    private static readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    private readonly AuthDbContext _context;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SigningKeyRing _keyRing;
    private readonly JwtOptions _jwt;
    private readonly TimeProvider _timeProvider;
    private readonly TokenValidationParameters _validation;

    public MfaTokenIssuer(
        AuthDbContext context,
        [FromKeyedServices(AuthDbContext.SchemaName)] IUnitOfWork unitOfWork,
        SigningKeyRing keyRing,
        IOptions<JwtOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = context;
        _unitOfWork = unitOfWork;
        _keyRing = keyRing;
        _jwt = options.Value;
        _timeProvider = timeProvider;
        _validation = new TokenValidationParameters
        {
            ValidIssuer = _jwt.Issuer,
            ValidAudience = Audience,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidAlgorithms = [SigningKeyRing.Algorithm],
            ValidTypes = [TokenType],
            ClockSkew = _jwt.ClockSkew,
            LifetimeValidator = (notBefore, expires, _, parameters) =>
                AuthenticationSetup.IsWithinLifetime(notBefore, expires, parameters.ClockSkew, _timeProvider.GetUtcNow()),
            IssuerSigningKeyResolver = (_, _, kid, _) => _keyRing.ResolveValidationKeys(kid),
            TryAllIssuerSigningKeys = false,
        };
    }

    /// <summary>The audience of mfa tokens, never equal to the audience of access tokens.</summary>
    public string Audience => _jwt.Audience + AudienceSuffix;

    /// <summary>
    /// Records a new challenge for <paramref name="userId"/> (deleting the user's expired ones) and returns its token.
    /// The caller has verified the password. Joins an open Auth transaction; a database outage is
    /// <see cref="ErrorType.Unavailable"/>.
    /// </summary>
    public async Task<Result<MfaToken>> IssueAsync(Guid userId, CancellationToken cancellationToken)
    {
        // JWT NumericDate has whole seconds; truncate so the challenge expiry equals the token's exp.
        var now = DateTimeOffset.FromUnixTimeSeconds(_timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var expiresAt = now + Lifetime;
        var challengeId = Guid.CreateVersion7(now);

        var recorded = await _unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                await _context.MfaChallenges
                    .Where(c => c.UserId == userId && c.ExpiresAt <= now)
                    .ExecuteDeleteAsync(token)
                    .ConfigureAwait(false);
                _context.MfaChallenges.Add(new MfaChallenge(challengeId, userId, expiresAt));
                return Result.Success();
            },
            retryOnTransientFailure: false,
            cancellationToken).ConfigureAwait(false);
        if (recorded.IsFailure)
        {
            return Result.Failure<MfaToken>(recorded.Error);
        }

        var descriptor = new SecurityTokenDescriptor
        {
            TokenType = TokenType,
            Issuer = _jwt.Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [AuthClaimTypes.Subject] = userId.ToString(),
                [AuthClaimTypes.JwtId] = challengeId.ToString(),
            },
            SigningCredentials = _keyRing.SigningCredentials,
        };

        return new MfaToken(_handler.CreateToken(descriptor), expiresAt);
    }

    /// <summary>
    /// The user and challenge of a valid mfa token (signature by a key of the ring, <c>typ</c>, issuer, audience,
    /// lifetime), or null. Whether the challenge is still open is the caller's check.
    /// </summary>
    public async Task<MfaTokenSubject?> ValidateAsync(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return null;
        }

        var result = await _handler.ValidateTokenAsync(token, _validation).ConfigureAwait(false);
        if (!result.IsValid
            || !Guid.TryParse(result.ClaimsIdentity.FindFirst(AuthClaimTypes.Subject)?.Value, out var userId)
            || !Guid.TryParse(result.ClaimsIdentity.FindFirst(AuthClaimTypes.JwtId)?.Value, out var challengeId))
        {
            return null;
        }

        return new MfaTokenSubject(userId, challengeId);
    }
}
