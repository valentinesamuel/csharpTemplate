using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Acme.Service.Modules.Auth.Tokens;

/// <summary>
/// Claim names of the access token. Inbound claims are not mapped (<c>MapInboundClaims=false</c>), so these short
/// names are also what <see cref="System.Security.Claims.ClaimsPrincipal"/> carries. <see cref="Permission"/> is the
/// claim the permission policies of BuildingBlocks.Web read (D-09).
/// </summary>
internal static class AuthClaimTypes
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string JwtId = JwtRegisteredClaimNames.Jti;
    public const string SessionId = JwtRegisteredClaimNames.Sid;
    public const string Email = JwtRegisteredClaimNames.Email;
    public const string Role = "role";
    public const string Permission = "perm";
    public const string AuthenticationMethod = JwtRegisteredClaimNames.Amr;
}

/// <summary>What an access token asserts about its holder. Permissions are computed from the roles at issue time (D-09).</summary>
internal sealed record AccessTokenSubject(
    Guid UserId,
    string? Email,
    Guid SessionId,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyCollection<string> AuthenticationMethods);

/// <summary>A signed access token and its expiry.</summary>
internal sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Creates RS256 access tokens with header <c>typ=at+jwt</c> (WH-14), signed by the Active key of the ring, with
/// claims <c>iss, aud, sub, jti, iat, nbf, exp, sid, email, role[], perm[], amr[]</c>. Stateless and CPU-only.
/// </summary>
internal sealed class AccessTokenIssuer(SigningKeyRing keyRing, IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    /// <summary>The JOSE <c>typ</c> of access tokens (RFC 9068); the only type accepted as a bearer token (INV-14).</summary>
    public const string TokenType = "at+jwt";

    // Times come from the injected TimeProvider, never from the handler's clock.
    private static readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public AccessToken Issue(AccessTokenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var jwt = options.Value;

        // JWT NumericDate has whole seconds; truncate so the reported expiry equals the token's exp.
        var now = DateTimeOffset.FromUnixTimeSeconds(timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var expiresAt = now + jwt.AccessTokenLifetime;

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [AuthClaimTypes.Subject] = subject.UserId.ToString(),
            [AuthClaimTypes.JwtId] = Guid.CreateVersion7().ToString("N"),
            [AuthClaimTypes.SessionId] = subject.SessionId.ToString(),
            [AuthClaimTypes.Role] = subject.Roles.ToArray(),
            [AuthClaimTypes.Permission] = subject.Permissions.ToArray(),
            [AuthClaimTypes.AuthenticationMethod] = subject.AuthenticationMethods.ToArray(),
        };
        if (!string.IsNullOrEmpty(subject.Email))
        {
            claims[AuthClaimTypes.Email] = subject.Email;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            TokenType = TokenType,
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = claims,
            SigningCredentials = keyRing.SigningCredentials,
        };

        return new AccessToken(_handler.CreateToken(descriptor), expiresAt);
    }
}
