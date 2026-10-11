using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Acme.Service.Testing.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Acme.Service.IntegrationTests.Fixtures;

/// <summary>
/// Builds valid and malicious JWTs for the negative tests, signed (unless stated) with the test host's runtime-generated
/// Active key (<see cref="TestSigningKeys"/>), so only the property under test is wrong.
/// </summary>
internal static class AccessTokenForge
{
    /// <summary>Issuer and audience of the test host (Host appsettings).</summary>
    public const string Issuer = "acme-service";

    /// <summary>Audience of the test host (Host appsettings).</summary>
    public const string Audience = "acme-service";

    /// <summary>The access token type (INV-14).</summary>
    public const string AccessTokenType = "at+jwt";

    private static readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>RS256 credentials of the test host's Active key.</summary>
    public static SigningCredentials ActiveKey()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(Encoding.UTF8.GetString(Convert.FromBase64String(TestSigningKeys.DefaultPrivateKeyBase64)));
        return new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = TestSigningKeys.DefaultKid }, SecurityAlgorithms.RsaSha256);
    }

    /// <summary>The PEM of the test host's public key (what JWKS publishes).</summary>
    public static string PublicKeyPem()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(Encoding.UTF8.GetString(Convert.FromBase64String(TestSigningKeys.DefaultPrivateKeyBase64)));
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>Creates a token; every argument defaults to a valid access token for the test host.</summary>
    public static string Create(
        SigningCredentials? credentials,
        string? type = AccessTokenType,
        string issuer = Issuer,
        string audience = Audience,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null,
        IEnumerable<string>? permissions = null)
    {
        var now = issuedAt ?? DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = Guid.CreateVersion7().ToString(),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["sid"] = Guid.CreateVersion7().ToString(),
            ["perm"] = (permissions ?? []).ToArray(),
            ["role"] = Array.Empty<string>(),
            ["amr"] = new[] { "pwd" },
        };

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = type,
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = (notBefore ?? now).UtcDateTime,
            Expires = (expires ?? now.AddMinutes(15)).UtcDateTime,
            Claims = claims,
            SigningCredentials = credentials,
        });
    }

    /// <summary>A valid access token of the test host.</summary>
    public static string Valid(params string[] permissions) => Create(ActiveKey(), permissions: permissions);

    /// <summary>An unsigned token (<c>alg=none</c>) with otherwise valid claims.</summary>
    public static string Unsigned() => Create(credentials: null);

    /// <summary>An HS256 token keyed with <paramref name="secret"/>, claiming the Active key's <c>kid</c> (key confusion).</summary>
    public static string Hs256(byte[] secret) =>
        Create(new SigningCredentials(new SymmetricSecurityKey(secret) { KeyId = TestSigningKeys.DefaultKid }, SecurityAlgorithms.HmacSha256));

    /// <summary><paramref name="token"/> with one bit of its signature flipped; header and payload unchanged.</summary>
    public static string TamperSignature(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var parts = token.Split('.');
        var signature = Base64Url.DecodeFromChars(parts[2]);
        signature[signature.Length / 2] ^= 0x01;
        return $"{parts[0]}.{parts[1]}.{Base64Url.EncodeToString(signature)}";
    }
}
