using System.Text.Json;
using Acme.Service.Modules.Auth.Tokens;
using Acme.Service.Testing.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Tokens;

public sealed class SigningKeyRingTests
{
    private static readonly Lazy<string> _next = new(() => TestSigningKeys.CreatePrivateKeyBase64());
    private static readonly Lazy<string> _retiring = new(() => TestSigningKeys.CreatePrivateKeyBase64(3072));

    [Fact]
    public void JwksJson_ActiveNextRetiring_PublishesPublicPartsOfAllKeys()
    {
        using var ring = CreateRing();

        using var jwks = JsonDocument.Parse(ring.JwksJson);
        var keys = jwks.RootElement.GetProperty("keys").EnumerateArray().ToList();

        keys.Select(k => k.GetProperty("kid").GetString()).ShouldBe(["active", "next", "retiring"]);
        foreach (var key in keys)
        {
            key.GetProperty("kty").GetString().ShouldBe("RSA");
            key.GetProperty("alg").GetString().ShouldBe("RS256");
            key.GetProperty("use").GetString().ShouldBe("sig");
            key.GetProperty("n").GetString().ShouldNotBeNullOrEmpty();
            key.GetProperty("e").GetString().ShouldNotBeNullOrEmpty();
            key.EnumerateObject().Select(p => p.Name).ShouldBe(["kty", "use", "alg", "kid", "n", "e"], ignoreOrder: true);
        }
    }

    [Fact]
    public void SigningCredentials_Ring_UsesActiveKeyWithRs256()
    {
        using var ring = CreateRing();

        ring.ActiveKid.ShouldBe("active");
        ring.SigningCredentials.Algorithm.ShouldBe("RS256");
    }

    [Theory]
    [InlineData("active")]
    [InlineData("next")]
    [InlineData("retiring")]
    public void ResolveValidationKeys_KnownKid_ReturnsPublicOnlyKey(string kid)
    {
        using var ring = CreateRing();

        var key = ring.ResolveValidationKeys(kid).ShouldHaveSingleItem();

        key.KeyId.ShouldBe(kid);
        var rsaKey = key.ShouldBeOfType<Microsoft.IdentityModel.Tokens.RsaSecurityKey>();
        Should.Throw<System.Security.Cryptography.CryptographicException>(() => rsaKey.Rsa.ExportParameters(includePrivateParameters: true));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("ACTIVE")]
    [InlineData(null)]
    public void ResolveValidationKeys_UnknownOrMissingKid_ReturnsNoKey(string? kid)
    {
        using var ring = CreateRing();

        ring.ResolveValidationKeys(kid).ShouldBeEmpty();
    }

    [Fact]
    public void Issue_Subject_CreatesRs256AtJwtWithAllClaims()
    {
        using var ring = CreateRing();
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, 500, TimeSpan.Zero);
        var issuer = new AccessTokenIssuer(ring, Microsoft.Extensions.Options.Options.Create(JwtOptionsFor()), new Fixtures.ManualTimeProvider(now));
        var userId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();

        var token = issuer.Issue(new AccessTokenSubject(userId, "a@example.com", sessionId, ["Staff"], ["roles.read", "users.read"], ["pwd"]));

        token.ExpiresAt.ShouldBe(new DateTimeOffset(2026, 3, 1, 12, 15, 0, TimeSpan.Zero));
        var jwt = new JsonWebToken(token.Token);
        jwt.Typ.ShouldBe("at+jwt");
        jwt.Alg.ShouldBe("RS256");
        jwt.Kid.ShouldBe("active");
        jwt.Issuer.ShouldBe("acme-service");
        jwt.Audiences.ShouldBe(["acme-service"]);
        jwt.Subject.ShouldBe(userId.ToString());
        jwt.Id.ShouldNotBeNullOrEmpty();
        jwt.GetClaim("sid").Value.ShouldBe(sessionId.ToString());
        jwt.GetClaim("email").Value.ShouldBe("a@example.com");
        jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value).ShouldBe(["Staff"]);
        jwt.Claims.Where(c => c.Type == "perm").Select(c => c.Value).ShouldBe(["roles.read", "users.read"]);
        jwt.Claims.Where(c => c.Type == "amr").Select(c => c.Value).ShouldBe(["pwd"]);
        jwt.IssuedAt.ShouldBe(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        jwt.ValidFrom.ShouldBe(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        jwt.ValidTo.ShouldBe(new DateTime(2026, 3, 1, 12, 15, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Constructor_UnloadableKey_Throws()
    {
        var options = JwtOptionsFor();
        options.SigningKeys[0].PrivateKeyBase64 = "@@@";

        Should.Throw<InvalidOperationException>(() => new SigningKeyRing(Microsoft.Extensions.Options.Options.Create(options)));
    }

    private static SigningKeyRing CreateRing() => new(Microsoft.Extensions.Options.Options.Create(JwtOptionsFor()));

    private static JwtOptions JwtOptionsFor()
    {
        var options = new JwtOptions { Issuer = "acme-service", Audience = "acme-service" };
        options.SigningKeys.Add(new SigningKeyOptions { Kid = "retiring", Status = "Retiring", PrivateKeyBase64 = _retiring.Value });
        options.SigningKeys.Add(new SigningKeyOptions { Kid = "active", Status = "Active", PrivateKeyBase64 = TestSigningKeys.DefaultPrivateKeyBase64 });
        options.SigningKeys.Add(new SigningKeyOptions { Kid = "next", Status = "Next", PrivateKeyBase64 = _next.Value });
        return options;
    }
}
