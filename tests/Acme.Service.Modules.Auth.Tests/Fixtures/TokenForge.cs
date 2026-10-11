using System.Security.Cryptography;
using Acme.Service.Modules.Auth.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>Builds arbitrary JWTs (valid and malicious) for the token validation tests.</summary>
internal static class TokenForge
{
    public const string Issuer = "acme-service";
    public const string Audience = "acme-service";

    private static readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>RS256 credentials over the base64 PEM <paramref name="privateKeyBase64"/>, with header <c>kid</c>.</summary>
    public static SigningCredentials RsaCredentials(string privateKeyBase64, string kid)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(privateKeyBase64)));
        return new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = kid }, SecurityAlgorithms.RsaSha256);
    }

    /// <summary>Creates a token; every argument defaults to a valid access token for the test host.</summary>
    public static string Create(
        SigningCredentials? credentials,
        string? type = AccessTokenIssuer.TokenType,
        string issuer = Issuer,
        string audience = Audience,
        DateTimeOffset? now = null,
        TimeSpan? lifetime = null,
        bool includeExpiry = true,
        Guid? sessionId = null,
        IEnumerable<string>? permissions = null)
    {
        var issuedAt = now ?? DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = Guid.CreateVersion7().ToString(),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["sid"] = (sessionId ?? Guid.CreateVersion7()).ToString(),
            ["perm"] = (permissions ?? []).ToArray(),
            ["role"] = Array.Empty<string>(),
            ["amr"] = new[] { "pwd" },
        };

        var descriptor = new SecurityTokenDescriptor
        {
            TokenType = type,
            Issuer = issuer,
            Audience = audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = includeExpiry ? (issuedAt + (lifetime ?? TimeSpan.FromMinutes(15))).UtcDateTime : null,
            Claims = claims,
            SigningCredentials = credentials,
        };

        return _handler.CreateToken(descriptor);
    }

    /// <summary>An unsigned token (<c>alg=none</c>) with otherwise valid claims.</summary>
    public static string Unsigned() => Create(credentials: null);

    /// <summary>An HS256 token whose HMAC key is <paramref name="secret"/>, claiming <paramref name="kid"/>.</summary>
    public static string Hs256(byte[] secret, string kid) =>
        Create(new SigningCredentials(new SymmetricSecurityKey(secret) { KeyId = kid }, SecurityAlgorithms.HmacSha256));
}
