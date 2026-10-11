using System.Net;
using Acme.Service.IntegrationTests.Fixtures;
using Shouldly;

namespace Acme.Service.IntegrationTests.Resilience;

/// <summary>
/// The Auth database becomes unreachable (container paused) while clients hold valid tokens (INV-13, D-29). Token
/// validation is stateless and keeps working; endpoints that opt into the <c>live-session</c> policy fail closed with
/// 503 <c>session_store_unavailable</c> (401 acceptable), never 2xx, and change nothing; after the outage the same calls
/// succeed. Each test owns its container, because pausing it is destructive.
/// </summary>
public sealed class AuthDbDownTests
{
    private const string LogoutAllPath = "/api/v1/auth/logout-all";
    private const string MfaSetupPath = "/api/v1/auth/mfa/authenticator";
    private const string RolesPath = "/api/v1/auth/roles";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AuthDbPaused_TokenValidation_StaysStateless()
    {
        await using var host = await PausablePostgresHost.StartAsync();
        var customer = await host.Api.UserAsync(Email("customer"));
        var admin = await host.Api.UserAsync(Email("admin"), "Admin");
        var expired = AccessTokenForge.Create(
            AccessTokenForge.ActiveKey(),
            issuedAt: DateTimeOffset.UtcNow.AddHours(-1),
            expires: DateTimeOffset.UtcNow.AddMinutes(-45));
        using (var warm = await host.Api.GetAsync(ClaimsProbeModule.ClaimsPath, customer.AccessToken))
        {
            warm.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await host.PauseAsync();
        using var customerClaims = await host.Api.GetAsync(ClaimsProbeModule.ClaimsPath, customer.AccessToken);
        using var adminClaims = await host.Api.GetAsync(ClaimsProbeModule.ClaimsPath, admin.AccessToken);
        using var tampered = await host.Api.GetAsync(ClaimsProbeModule.ClaimsPath, AccessTokenForge.TamperSignature(customer.AccessToken));
        using var expiredResponse = await host.Api.GetAsync(ClaimsProbeModule.ClaimsPath, expired);
        using var anonymous = await host.Api.GetAsync(ClaimsProbeModule.ClaimsPath);
        await host.UnpauseAsync();

        customerClaims.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ServiceApi.JsonAsync(customerClaims)).GetProperty("sub").GetString().ShouldBe(customer.Id.ToString());
        adminClaims.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ServiceApi.JsonAsync(adminClaims)).GetProperty("perms").EnumerateArray().Select(p => p.GetString())
            .ShouldContain("roles.read");
        tampered.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        expiredResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AuthDbPaused_LiveSessionEndpoints_FailClosedWithoutSideEffectsAndRecover()
    {
        await using var host = await PausablePostgresHost.StartAsync();
        var customer = await host.Api.UserAsync(Email("customer"));
        var admin = await host.Api.UserAsync(Email("admin"), "Admin");
        using (var setup = await host.Api.PostAsync(MfaSetupPath, null, customer.AccessToken))
        {
            setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var keyBefore = await AuthenticatorKeyAsync(host, customer);
        var stampBefore = await SecurityStampAsync(host, customer);
        keyBefore.ShouldNotBeNullOrEmpty();

        await host.PauseAsync();
        using var logoutAll = await host.Api.PostAsync(LogoutAllPath, null, customer.AccessToken);
        using var mfaSetup = await host.Api.PostAsync(MfaSetupPath, null, customer.AccessToken);
        using var roles = await host.Api.GetAsync(RolesPath, admin.AccessToken);
        await host.UnpauseAsync();

        await ShouldFailClosedAsync(logoutAll);
        await ShouldFailClosedAsync(mfaSetup);
        await ShouldFailClosedAsync(roles);

        // Nothing happened during the outage: no session revoked, the authenticator key and the security stamp unchanged.
        (await RevokedSessionsAsync(host, customer)).ShouldBe(0);
        (await RevokedSessionsAsync(host, admin)).ShouldBe(0);
        (await AuthenticatorKeyAsync(host, customer)).ShouldBe(keyBefore);
        (await SecurityStampAsync(host, customer)).ShouldBe(stampBefore);

        // The same calls succeed once the database is back (logout-all last: it ends the customer's session).
        using var rolesAfter = await ServiceApi.UntilStatusAsync(() => host.Api.GetAsync(RolesPath, admin.AccessToken), HttpStatusCode.OK, "roles are listed");
        using var setupAfter = await ServiceApi.UntilStatusAsync(() => host.Api.PostAsync(MfaSetupPath, null, customer.AccessToken), HttpStatusCode.OK, "the authenticator is reset");
        using var logoutAllAfter = await ServiceApi.UntilStatusAsync(() => host.Api.PostAsync(LogoutAllPath, null, customer.AccessToken), HttpStatusCode.NoContent, "logout-all succeeds");
        (await AuthenticatorKeyAsync(host, customer)).ShouldNotBe(keyBefore);
        (await RevokedSessionsAsync(host, customer)).ShouldBe(1);
        (await RevokedSessionsAsync(host, admin)).ShouldBe(0);
    }

    private static string Email(string name) => $"{name}-{Guid.NewGuid():N}@example.com";

    private static async Task ShouldFailClosedAsync(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.ShouldBeFalse();
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            (await ServiceApi.ProblemCodeAsync(response)).ShouldBe("session_store_unavailable");
            response.Headers.RetryAfter.ShouldNotBeNull();
        }
    }

    private static Task<long> RevokedSessionsAsync(PausablePostgresHost host, TestUser user) =>
        host.Api.ScalarAsync<long>("SELECT count(*) FROM auth.sessions WHERE user_id = @id AND revoked_at IS NOT NULL", ("id", user.Id));

    private static Task<string?> AuthenticatorKeyAsync(PausablePostgresHost host, TestUser user) =>
        host.Api.ScalarAsync<string?>("SELECT string_agg(value, ',') FROM auth.user_tokens WHERE user_id = @id AND name = 'AuthenticatorKey'", ("id", user.Id));

    private static Task<string?> SecurityStampAsync(PausablePostgresHost host, TestUser user) =>
        host.Api.ScalarAsync<string?>("SELECT security_stamp FROM auth.users WHERE id = @id", ("id", user.Id));
}
