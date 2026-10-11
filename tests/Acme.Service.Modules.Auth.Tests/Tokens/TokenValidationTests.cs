using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Security;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Tokens;

/// <summary>One host whose ring holds an Active (the default test key), a Next and a Retiring key.</summary>
public sealed class KeyRingHostFixture : IAsyncLifetime
{
    public const string NextKid = "test-next";
    public const string RetiringKid = "test-retiring";

    public string NextKey { get; } = TestSigningKeys.CreatePrivateKeyBase64();

    public string RetiringKey { get; } = TestSigningKeys.CreatePrivateKeyBase64();

    internal ProbeWebFactory Factory { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in TestSigningKeys.Entry(1, NextKid, "Next", NextKey).Concat(TestSigningKeys.Entry(2, RetiringKid, "Retiring", RetiringKey)))
        {
            settings[key] = value;
        }

        Factory = new ProbeWebFactory(overrides: settings);
        _ = Factory.Services;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>
/// Stateless JWT validation through the real Host pipeline (INV-13, INV-14, WH-14, WH-15). No database is configured,
/// which also proves validation never touches it.
/// </summary>
public sealed class TokenValidationTests(KeyRingHostFixture host) : IClassFixture<KeyRingHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProbeWebFactory Factory => host.Factory;

    [Fact]
    public async Task GetJwks_Anonymous_ListsActiveNextRetiringPublicKeysOnly()
    {
        using var response = await ProbeClient.GetAsync(Factory, "/.well-known/jwks.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.ToString().Replace(" ", string.Empty, StringComparison.Ordinal).ShouldBe("public,max-age=300");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        using var jwks = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var keys = jwks.RootElement.GetProperty("keys").EnumerateArray().ToList();
        keys.Select(k => k.GetProperty("kid").GetString()).ShouldBe([TestSigningKeys.DefaultKid, KeyRingHostFixture.NextKid, KeyRingHostFixture.RetiringKid]);
        foreach (var key in keys)
        {
            key.GetProperty("alg").GetString().ShouldBe("RS256");
            key.GetProperty("use").GetString().ShouldBe("sig");
            var names = key.EnumerateObject().Select(p => p.Name).ToList();
            names.ShouldNotContain("d");
            names.ShouldNotContain("p");
            names.ShouldNotContain("q");
            names.ShouldNotContain("dp");
            names.ShouldNotContain("dq");
            names.ShouldNotContain("qi");
        }
    }

    [Fact]
    public async Task GetJwks_PublishedModulus_MatchesActiveKey()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(TestSigningKeys.DefaultPrivateKeyBase64)));

        using var response = await ProbeClient.GetAsync(Factory, "/.well-known/jwks.json");
        using var jwks = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        var active = jwks.RootElement.GetProperty("keys")[0];
        active.GetProperty("n").GetString().ShouldBe(Base64Url.EncodeToString(rsa.ExportParameters(false).Modulus));
    }

    [Fact]
    public async Task Get_IssuedAccessToken_Returns200WithClaims()
    {
        var sessionId = Guid.CreateVersion7();
        var token = ProbeClient.IssueAccessToken(Factory, sessionId, "users.read");

        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var claims = await response.Content.ReadFromJsonAsync<ProbeClaims>(Ct);
        claims.ShouldNotBeNull();
        claims.Sid.ShouldBe(sessionId.ToString());
        claims.Sub.ShouldNotBeNullOrEmpty();
        claims.Email.ShouldBe("probe@example.com");
        claims.Perms.ShouldBe(["users.read"]);
        claims.Amr.ShouldBe(["pwd"]);
    }

    [Fact]
    public async Task Get_TokenSignedByRetiringKey_Returns200()
    {
        var token = TokenForge.Create(TokenForge.RsaCredentials(host.RetiringKey, KeyRingHostFixture.RetiringKid));

        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_TokenSignedByNextKey_Returns200()
    {
        var token = TokenForge.Create(TokenForge.RsaCredentials(host.NextKey, KeyRingHostFixture.NextKid));

        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_NoToken_Returns401ProblemWithBearerChallenge()
    {
        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath);

        await ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Get_EndpointWithoutMetadataNoToken_Returns401FromFallbackPolicy()
    {
        using var anonymous = await ProbeClient.GetAsync(Factory, ProbeModule.FallbackPath);
        using var authenticated = await ProbeClient.GetAsync(Factory, ProbeModule.FallbackPath, ProbeClient.IssueAccessToken(Factory));

        await ShouldBeUnauthorizedAsync(anonymous);
        authenticated.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_UnknownKid_Returns401()
    {
        var token = TokenForge.Create(TokenForge.RsaCredentials(TestSigningKeys.CreatePrivateKeyBase64(), "unknown-kid"));

        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, token);

        await ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Get_KnownKidSignedByOtherKey_Returns401()
    {
        var token = TokenForge.Create(TokenForge.RsaCredentials(TestSigningKeys.CreatePrivateKeyBase64(), TestSigningKeys.DefaultKid));

        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, token);

        await ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Get_AlgNone_Returns401()
    {
        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, TokenForge.Unsigned());

        await ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Get_Hs256WithPublicModulusAsSecret_Returns401()
    {
        // Algorithm confusion: an HMAC "signed" with public key material, claiming the Active kid.
        using var rsa = RSA.Create();
        rsa.ImportFromPem(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(TestSigningKeys.DefaultPrivateKeyBase64)));
        var token = TokenForge.Hs256(rsa.ExportParameters(false).Modulus!, TestSigningKeys.DefaultKid);

        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, token);

        await ShouldBeUnauthorizedAsync(response);
    }

    [Theory]
    [InlineData("JWT")]
    [InlineData("mfa+jwt")]
    [InlineData(null)]
    public async Task Get_WrongTyp_Returns401(string? type)
    {
        // INV-14: only typ=at+jwt authenticates; MFA and other token types never work as bearer tokens.
        var token = TokenForge.Create(TokenForge.RsaCredentials(TestSigningKeys.DefaultPrivateKeyBase64, TestSigningKeys.DefaultKid), type: type);

        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, token);

        await ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Get_WrongAudience_Returns401()
    {
        var token = TokenForge.Create(TokenForge.RsaCredentials(TestSigningKeys.DefaultPrivateKeyBase64, TestSigningKeys.DefaultKid), audience: "other-api");

        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, token);

        await ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Get_WrongIssuer_Returns401()
    {
        var token = TokenForge.Create(TokenForge.RsaCredentials(TestSigningKeys.DefaultPrivateKeyBase64, TestSigningKeys.DefaultKid), issuer: "https://evil.example");

        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, token);

        await ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Get_NoExpiry_Returns401()
    {
        var token = TokenForge.Create(TokenForge.RsaCredentials(TestSigningKeys.DefaultPrivateKeyBase64, TestSigningKeys.DefaultKid), includeExpiry: false);

        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, token);

        await ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Get_RefreshTokenAsBearer_Returns401()
    {
        // INV-14: a refresh token (32 random bytes, base64url) is never a bearer token.
        var refreshToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.AuthenticatedPath, refreshToken);

        await ShouldBeUnauthorizedAsync(response);
    }

    [Fact]
    public async Task GetPermissionEndpoint_TokenWithPermClaim_Returns200()
    {
        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.PermissionPath, ProbeClient.IssueAccessToken(Factory, null, "users.read"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPermissionEndpoint_TokenWithoutPermClaim_Returns403()
    {
        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.PermissionPath, ProbeClient.IssueAccessToken(Factory, null, "users.manage", "roles.read"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProbeClient.ProblemCodeAsync(response)).ShouldBe("forbidden");
    }

    [Fact]
    public async Task GetPermissionEndpoint_NoToken_Returns401()
    {
        using var response = await ProbeClient.GetAsync(Factory, ProbeModule.PermissionPath);

        await ShouldBeUnauthorizedAsync(response);
    }

    internal static async Task ShouldBeUnauthorizedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldContain(h => h.Scheme == "Bearer");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await ProbeClient.ProblemCodeAsync(response)).ShouldBe("unauthorized");
    }
}
