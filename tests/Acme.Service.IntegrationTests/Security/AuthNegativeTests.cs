using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Acme.Service.IntegrationTests.Fixtures;
using Acme.Service.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.IntegrationTests.Security;

/// <summary>
/// P19 negative tests over the real host (INV-12, INV-14, INV-15, D-11, D-12, D-23): forged and malformed tokens,
/// token-type confusion, refresh token reuse, lockout, login rate limiting, missing permissions, and every endpoint not in
/// <see cref="AnonymousEndpointAllowlist"/> refusing anonymous callers.
/// </summary>
public sealed class AuthNegativeTests(MigratedHostFixture host) : IClassFixture<MigratedHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ForgedToken_ValidBaseline_Returns200()
    {
        // The forge itself must produce accepted tokens, or the negative cases below would prove nothing.
        using var response = await host.Api.GetAsync(ClaimsProbeModule.ClaimsPath, AccessTokenForge.Valid("users.read"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadJsonAsync(Ct)).GetProperty("perms")[0].GetString().ShouldBe("users.read");
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("tampered-signature")]
    [InlineData("alg-none")]
    [InlineData("hs256-public-key-pem")]
    [InlineData("hs256-public-key-der")]
    [InlineData("foreign-key-same-kid")]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("typ-jwt")]
    [InlineData("typ-mfa")]
    [InlineData("no-typ")]
    public async Task ForgedToken_Invalid_Returns401(string variant)
    {
        var token = Forge(variant);

        using var claims = await host.Api.GetAsync(ClaimsProbeModule.ClaimsPath, token);
        using var me = await host.Api.GetAsync("/api/v1/auth/me", token);

        await ShouldBeUnauthorizedAsync(claims);
        await ShouldBeUnauthorizedAsync(me);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IssuedAccessToken_SignatureTampered_Returns401()
    {
        var user = await host.Api.UserAsync(Email("tamper"));

        using var valid = await host.Api.GetAsync("/api/v1/auth/me", user.AccessToken);
        using var tampered = await host.Api.GetAsync("/api/v1/auth/me", AccessTokenForge.TamperSignature(user.AccessToken));

        valid.StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldBeUnauthorizedAsync(tampered);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MfaToken_UsedAsAccessToken_Returns401()
    {
        var email = Email("mfa-bearer");
        var user = await host.Api.UserAsync(email);
        await host.Api.EnableMfaAsync(user.AccessToken);
        var mfaToken = await host.Api.LoginMfaTokenAsync(email);

        using var claims = await host.Api.GetAsync(ClaimsProbeModule.ClaimsPath, mfaToken);
        using var me = await host.Api.GetAsync("/api/v1/auth/me", mfaToken);

        await ShouldBeUnauthorizedAsync(claims);
        await ShouldBeUnauthorizedAsync(me);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshToken_UsedAsAccessToken_Returns401()
    {
        var user = await host.Api.UserAsync(Email("refresh-bearer"));

        using var claims = await host.Api.GetAsync(ClaimsProbeModule.ClaimsPath, user.RefreshToken);

        await ShouldBeUnauthorizedAsync(claims);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshToken_Reused_RevokesTheWholeFamily()
    {
        var user = await host.Api.UserAsync(Email("reuse"));
        using var first = await host.Api.RefreshAsync(user.RefreshToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = await ServiceApi.TokensOfAsync(first);

        using var reuse = await host.Api.RefreshAsync(user.RefreshToken);
        using var successor = await host.Api.RefreshAsync(rotated.RefreshToken);
        using var liveSession = await host.Api.PostAsync("/api/v1/auth/logout-all", null, rotated.AccessToken);

        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ServiceApi.ProblemCodeAsync(reuse)).ShouldBe("invalid_refresh_token");
        successor.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ServiceApi.ProblemCodeAsync(successor)).ShouldBe("invalid_refresh_token");
        liveSession.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ServiceApi.ProblemCodeAsync(liveSession)).ShouldBe("session_revoked");
        (await host.Api.ScalarAsync<string>(
            "SELECT revoked_reason FROM auth.sessions WHERE user_id = @id",
            ("id", user.Id))).ShouldBe("reuse_detected");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Login_LockedOutUser_Returns401InvalidCredentialsEvenWithTheRightPassword()
    {
        var email = Email("locked");
        await host.Api.UserAsync(email);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await host.Api.LoginAsync(email, "wrong password, wrong");
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var right = await host.Api.LoginAsync(email);

        right.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ServiceApi.ProblemCodeAsync(right)).ShouldBe("invalid_credentials");
        right.Headers.Contains("Set-Cookie").ShouldBeFalse();
        (await host.Api.ScalarAsync<bool>(
            "SELECT lockout_end > now() FROM auth.users WHERE normalized_email = @email",
            ("email", email.ToUpperInvariant()))).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Login_OverTheCredentialsRateLimit_Returns429()
    {
        var connectionString = await host.Postgres.CreateDatabaseAsync(Ct);
        await ServiceApi.MigrateAsync(connectionString);
        await using var factory = new IntegrationWebFactory(connectionString, new Dictionary<string, string?>
        {
            ["Auth:RateLimiting:Credentials:PermitLimit"] = "3",
            ["Auth:RateLimiting:Credentials:Window"] = "00:10:00",
        });
        var api = new ServiceApi(factory);

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await api.LoginAsync("nobody@example.com");
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                (await ServiceApi.ProblemCodeAsync(response)).ShouldBe("too_many_requests");
            }
        }

        statuses.ShouldBe([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AdminEndpoint_CustomerWithoutPermission_Returns403()
    {
        var customer = await host.Api.UserAsync(Email("no-perm"));
        var staff = await host.Api.UserAsync(Email("staff"), "Staff");

        using var forbidden = await host.Api.GetAsync("/api/v1/auth/roles", customer.AccessToken);
        using var allowed = await host.Api.GetAsync("/api/v1/auth/roles", staff.AccessToken);

        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Endpoints_NotAllowlisted_Return401WithoutAToken()
    {
        var endpoints = host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && !AnonymousEndpointAllowlist.Patterns.ContainsKey(raw))
            .ToList();
        // At least the Auth module's protected endpoints (14) plus the probe; holds with the Orders sample removed.
        endpoints.Count.ShouldBeGreaterThan(10);
        endpoints.ShouldContain(e => e.RoutePattern.RawText == "/api/v1/auth/logout-all");
        endpoints.ShouldContain(e => e.RoutePattern.RawText == ClaimsProbeModule.ClaimsPath);

        var failures = new List<string>();
        var checkedRequests = 0;
        foreach (var endpoint in endpoints)
        {
            var pattern = endpoint.RoutePattern.RawText!;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [HttpMethods.Get];
            foreach (var method in methods)
            {
                var probe = Guid.NewGuid().ToString("N");
                using var response = await host.Api.SendAsync(
                    new HttpMethod(method),
                    PathOf(endpoint.RoutePattern),
                    accessToken: null,
                    content: null,
                    (IntegrationWebFactory.ProbeHeader, probe));
                checkedRequests++;

                host.Factory.MatchedEndpoints.TryGetValue(probe, out var matched);
                if (response.StatusCode != HttpStatusCode.Unauthorized || matched != pattern)
                {
                    failures.Add($"{method} {pattern}: {(int)response.StatusCode}, matched '{matched}'");
                }
            }
        }

        checkedRequests.ShouldBeGreaterThanOrEqualTo(endpoints.Count);
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    private static string Email(string name) => $"{name}-{Guid.NewGuid():N}@example.com";

    private static string Forge(string variant)
    {
        var now = DateTimeOffset.UtcNow;
        return variant switch
        {
            "tampered-signature" => AccessTokenForge.TamperSignature(AccessTokenForge.Valid()),
            "alg-none" => AccessTokenForge.Unsigned(),
            "hs256-public-key-pem" => AccessTokenForge.Hs256(Encoding.UTF8.GetBytes(AccessTokenForge.PublicKeyPem())),
            "hs256-public-key-der" => AccessTokenForge.Hs256(PublicKeyDer()),
            "foreign-key-same-kid" => AccessTokenForge.Create(ForeignKeyWithActiveKid()),
            "expired" => AccessTokenForge.Create(AccessTokenForge.ActiveKey(), issuedAt: now.AddHours(-1), expires: now.AddMinutes(-45)),
            "not-yet-valid" => AccessTokenForge.Create(AccessTokenForge.ActiveKey(), notBefore: now.AddMinutes(10), expires: now.AddMinutes(25)),
            "wrong-issuer" => AccessTokenForge.Create(AccessTokenForge.ActiveKey(), issuer: "https://issuer.example.test"),
            "wrong-audience" => AccessTokenForge.Create(AccessTokenForge.ActiveKey(), audience: "another-api"),
            "typ-jwt" => AccessTokenForge.Create(AccessTokenForge.ActiveKey(), type: "JWT"),
            "typ-mfa" => AccessTokenForge.Create(AccessTokenForge.ActiveKey(), type: "mfa+jwt"),
            "no-typ" => AccessTokenForge.Create(AccessTokenForge.ActiveKey(), type: null),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown variant."),
        };
    }

    private static byte[] PublicKeyDer()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(AccessTokenForge.PublicKeyPem());
        return rsa.ExportSubjectPublicKeyInfo();
    }

    private static Microsoft.IdentityModel.Tokens.SigningCredentials ForeignKeyWithActiveKid() =>
        new(
            new Microsoft.IdentityModel.Tokens.RsaSecurityKey(RSA.Create(2048)) { KeyId = Acme.Service.Testing.Security.TestSigningKeys.DefaultKid },
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256);

    private static async Task ShouldBeUnauthorizedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.WwwAuthenticate.ShouldContain(h => h.Scheme == "Bearer");
    }

    /// <summary>A concrete path matching <paramref name="pattern"/>: a Guid for guid parameters, a number for numeric ones, else a word.</summary>
    private static string PathOf(RoutePattern pattern)
    {
        var builder = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            builder.Append('/');
            foreach (var part in segment.Parts)
            {
                builder.Append(part switch
                {
                    RoutePatternLiteralPart literal => literal.Content,
                    RoutePatternSeparatorPart separator => separator.Content,
                    RoutePatternParameterPart parameter => ValueOf(parameter),
                    _ => throw new InvalidOperationException($"Unexpected route part in '{pattern.RawText}'."),
                });
            }
        }

        return builder.Length == 0 ? "/" : builder.ToString();
    }

    private static string ValueOf(RoutePatternParameterPart parameter)
    {
        var policies = parameter.ParameterPolicies.Select(p => p.Content).ToList();
        if (policies.Contains("guid"))
        {
            return Guid.CreateVersion7().ToString();
        }

        return policies.Exists(p => p is "int" or "long")
            ? 1.ToString(CultureInfo.InvariantCulture)
            : "x";
    }
}
