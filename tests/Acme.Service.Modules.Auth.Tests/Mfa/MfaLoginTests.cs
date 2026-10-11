using System.Net;
using System.Net.Http.Json;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Mfa;

/// <summary>
/// TOTP two-factor authentication end to end (D-15, INV-14, WH-11): setup and verification, the mfa step of the
/// sign-in, the challenge's attempt limit, replay protection, recovery codes, lockout and disabling. Codes are computed
/// in the test (RFC 6238) at the server's injected clock.
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class MfaLoginTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EnableMfa_ThenLogin_ReturnsMfaRequiredAndCodeCompletesSignInWithMfaAmr()
    {
        var (factory, clock) = await MfaTestHost.CreateAsync(postgres);
        await using var _ = factory;
        using var client = new AccountClient(factory);
        var userId = await client.RegisterConfirmedAsync("mfa@example.com");
        var tokens = await client.LoginTokensAsync("mfa@example.com");

        using var setup = await client.MfaSetupAsync(tokens.AccessToken);
        var key = (await setup.Content.ReadDataAsync<MfaSetupBody>(Ct))!;
        using var verify = await client.MfaVerifyAsync(tokens.AccessToken, Rfc6238.Code(key.SharedKey, clock.GetUtcNow()));
        var codes = (await verify.Content.ReadDataAsync<RecoveryCodesBody>(Ct))!.RecoveryCodes;
        using var me = await client.MeAsync(tokens.AccessToken);
        using var setupAgain = await client.MfaSetupAsync(tokens.AccessToken);
        clock.Advance(Rfc6238.Step);
        var mfaToken = await client.LoginMfaTokenAsync("mfa@example.com");
        using var complete = await client.LoginMfaAsync(mfaToken, code: Rfc6238.Code(key.SharedKey, clock.GetUtcNow()));
        var signedIn = (await complete.Content.ReadDataAsync<TokenBody>(Ct))!;
        using var meAfter = await client.MeAsync(signedIn.AccessToken);

        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        key.SharedKey.ShouldMatch("^[A-Z2-7]{32}$");
        var uri = new Uri(key.AuthenticatorUri);
        uri.Scheme.ShouldBe("otpauth");
        key.AuthenticatorUri.ShouldStartWith("otpauth://totp/acme-service:mfa%40example.com?");
        var query = QueryHelpers.ParseQuery(uri.Query);
        query["secret"].ToString().ShouldBe(key.SharedKey);
        query["issuer"].ToString().ShouldBe("acme-service");
        query["digits"].ToString().ShouldBe("6");
        query["period"].ToString().ShouldBe("30");

        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        codes.Count.ShouldBe(10);
        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(10);
        (await me.Content.ReadAsStringAsync(Ct)).ShouldContain("\"mfaEnabled\":true");
        setupAgain.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AccountClient.ProblemCodeAsync(setupAgain)).ShouldBe("mfa_already_enabled");

        // D-15: neither the key nor the codes are stored in plaintext.
        var stored = await AuthTestServices.RowsAsync(factory.ConnectionString, "SELECT name, value FROM auth.user_tokens");
        stored.ShouldNotContain(row => row.Contains(key.SharedKey, StringComparison.Ordinal));
        stored.ShouldNotContain(row => codes.Any(c => row.Contains(c, StringComparison.Ordinal)));

        complete.StatusCode.ShouldBe(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync(Ct));
        new JsonWebToken(signedIn.AccessToken).Claims.Where(c => c.Type == "amr").Select(c => c.Value).ShouldBe(["pwd", "mfa"]);
        meAfter.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AuthTestServices.ScalarAsync<long>(
            factory.ConnectionString,
            "SELECT count(*) FROM auth.mfa_challenges WHERE user_id = @id AND consumed_at IS NOT NULL",
            ("id", userId))).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MfaToken_UsedAsBearerToken_Returns401()
    {
        var (factory, clock) = await MfaTestHost.CreateAsync(postgres);
        await using var _ = factory;
        using var client = new AccountClient(factory);
        await MfaTestHost.EnableMfaAsync(client, clock, "bearer@example.com");
        var mfaToken = await client.LoginMfaTokenAsync("bearer@example.com");

        using var me = await client.MeAsync(mfaToken);
        using var logoutAll = await client.LogoutAllAsync(mfaToken);
        using var setup = await client.MfaSetupAsync(mfaToken);

        // INV-14: an mfa token never authenticates an API call.
        new JsonWebToken(mfaToken).Typ.ShouldBe("mfa+jwt");
        new JsonWebToken(mfaToken).Audiences.ShouldBe(["acme-service:mfa"]);
        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        logoutAll.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        setup.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LoginMfa_AccessTokenOrForeignTokenAsMfaToken_Returns401InvalidMfaToken()
    {
        var (factory, clock) = await MfaTestHost.CreateAsync(postgres);
        await using var _ = factory;
        using var client = new AccountClient(factory);
        var (_, key, _) = await MfaTestHost.EnableMfaAsync(client, clock, "swap@example.com");
        await client.RegisterConfirmedAsync("plain@example.com");
        var access = await client.LoginTokensAsync("plain@example.com");
        var code = Rfc6238.Code(key, clock.GetUtcNow());

        using var withAccessToken = await client.LoginMfaAsync(access.AccessToken, code: code);
        using var withGarbage = await client.LoginMfaAsync("not-a-token", code: code);

        withAccessToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(withAccessToken)).ShouldBe("invalid_mfa_token");
        withGarbage.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(withGarbage)).ShouldBe("invalid_mfa_token");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LoginMfa_WrongCodeFiveTimes_KillsTheChallenge()
    {
        // Lockout above the challenge limit, so the challenge's own limit is what stops the sixth attempt.
        var (factory, clock) = await MfaTestHost.CreateAsync(
            postgres,
            new Dictionary<string, string?> { ["Auth:Identity:Lockout:MaxFailedAccessAttempts"] = "20" });
        await using var _ = factory;
        using var client = new AccountClient(factory);
        var (userId, key, _) = await MfaTestHost.EnableMfaAsync(client, clock, "brute@example.com");
        var mfaToken = await client.LoginMfaTokenAsync("brute@example.com");

        var failures = new List<string?>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await client.LoginMfaAsync(mfaToken, code: Rfc6238.WrongCode(key, clock.GetUtcNow()));
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            failures.Add(await AccountClient.ProblemCodeAsync(wrong));
        }

        using var correct = await client.LoginMfaAsync(mfaToken, code: Rfc6238.Code(key, clock.GetUtcNow()));
        var freshToken = await client.LoginMfaTokenAsync("brute@example.com");
        using var fresh = await client.LoginMfaAsync(freshToken, code: Rfc6238.Code(key, clock.GetUtcNow()));

        failures.ShouldAllBe(code => code == "invalid_mfa_code");
        correct.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(correct)).ShouldBe("invalid_mfa_token");
        (await AuthTestServices.RowsAsync(factory.ConnectionString, "SELECT attempts, consumed_at IS NULL FROM auth.mfa_challenges ORDER BY attempts DESC"))
            .ShouldBe(["5|True", "0|False"]);

        // The code was not used up by the dead challenge, so a new challenge accepts it; success resets the counter.
        fresh.StatusCode.ShouldBe(HttpStatusCode.OK, await fresh.Content.ReadAsStringAsync(Ct));
        (await AuthTestServices.ScalarAsync<int>(
            factory.ConnectionString, "SELECT access_failed_count FROM auth.users WHERE id = @id", ("id", userId))).ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LoginMfa_WrongCodes_LockTheAccount()
    {
        var (factory, clock) = await MfaTestHost.CreateAsync(postgres);
        await using var _ = factory;
        using var client = new AccountClient(factory);
        var (userId, key, _) = await MfaTestHost.EnableMfaAsync(client, clock, "locked@example.com");

        // Five wrong codes over two challenges (Identity lockout: 5 failures).
        var first = await client.LoginMfaTokenAsync("locked@example.com");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var wrong = await client.LoginMfaAsync(first, code: Rfc6238.WrongCode(key, clock.GetUtcNow()));
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var second = await client.LoginMfaTokenAsync("locked@example.com");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var wrong = await client.LoginMfaAsync(second, code: Rfc6238.WrongCode(key, clock.GetUtcNow()));
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var correctCode = await client.LoginMfaAsync(second, code: Rfc6238.Code(key, clock.GetUtcNow()));
        using var password = await client.LoginAsync("locked@example.com");

        correctCode.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(correctCode)).ShouldBe("invalid_mfa_code");
        password.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(password)).ShouldBe("invalid_credentials");
        (await AuthTestServices.ScalarAsync<DateTime>(
            factory.ConnectionString, "SELECT lockout_end FROM auth.users WHERE id = @id", ("id", userId)))
            .ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(10));
        (await AuthTestServices.ScalarAsync<long>(factory.ConnectionString, "SELECT count(*) FROM auth.sessions WHERE amr @> ARRAY['mfa']"))
            .ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LoginMfa_SameValidCodeTwice_SecondIs401()
    {
        var (factory, clock) = await MfaTestHost.CreateAsync(postgres);
        await using var _ = factory;
        using var client = new AccountClient(factory);
        var (_, key, _) = await MfaTestHost.EnableMfaAsync(client, clock, "replay@example.com");
        var code = Rfc6238.Code(key, clock.GetUtcNow());
        var first = await client.LoginMfaTokenAsync("replay@example.com");
        var second = await client.LoginMfaTokenAsync("replay@example.com");

        using var accepted = await client.LoginMfaAsync(first, code: code);
        using var replayed = await client.LoginMfaAsync(second, code: code);
        clock.Advance(Rfc6238.Step);
        using var older = await client.LoginMfaAsync(second, code: code);
        using var next = await client.LoginMfaAsync(second, code: Rfc6238.Code(key, clock.GetUtcNow()));

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(replayed)).ShouldBe("invalid_mfa_code");

        // Still inside the drift window, but its step was used: refused. The next step's code works.
        older.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        next.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IdentityAuthenticatorProvider_SameCodeTwice_AcceptsBoth()
    {
        // WH-11: Identity's own verification does not stop reuse within the window, hence TotpReplayGuard.
        var (factory, _) = await MfaTestHost.CreateAsync(postgres);
        await using var _ = factory;
        var userId = await TestUsers.CreateAsync(factory.Services, "wh11@example.com");
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var user = (await userManager.FindByIdAsync(userId.ToString()))!;
        (await userManager.ResetAuthenticatorKeyAsync(user)).Succeeded.ShouldBeTrue();
        var key = (await userManager.GetAuthenticatorKeyAsync(user))!;

        // Identity reads the system clock.
        var code = Rfc6238.Code(key, DateTimeOffset.UtcNow);
        var provider = userManager.Options.Tokens.AuthenticatorTokenProvider;

        (await userManager.VerifyTwoFactorTokenAsync(user, provider, code)).ShouldBeTrue();
        (await userManager.VerifyTwoFactorTokenAsync(user, provider, code)).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LoginMfa_RecoveryCode_WorksOnceAndRegeneratingInvalidatesTheOldOnes()
    {
        var (factory, clock) = await MfaTestHost.CreateAsync(postgres);
        await using var _ = factory;
        using var client = new AccountClient(factory);
        var (_, _, codes) = await MfaTestHost.EnableMfaAsync(client, clock, "recovery@example.com");

        using var first = await client.LoginMfaAsync(await client.LoginMfaTokenAsync("recovery@example.com"), recoveryCode: codes[0]);
        using var again = await client.LoginMfaAsync(await client.LoginMfaTokenAsync("recovery@example.com"), recoveryCode: codes[0]);
        using var other = await client.LoginMfaAsync(await client.LoginMfaTokenAsync("recovery@example.com"), recoveryCode: codes[1]);
        var session = (await other.Content.ReadDataAsync<TokenBody>(Ct))!;
        using var wrongPassword = await client.MfaRecoveryCodesAsync(session.AccessToken, "not the right password");
        using var regenerate = await client.MfaRecoveryCodesAsync(session.AccessToken);
        regenerate.StatusCode.ShouldBe(HttpStatusCode.OK, await regenerate.Content.ReadAsStringAsync(Ct));
        var renewed = (await regenerate.Content.ReadDataAsync<RecoveryCodesBody>(Ct))!.RecoveryCodes;
        using var oldCode = await client.LoginMfaAsync(await client.LoginMfaTokenAsync("recovery@example.com"), recoveryCode: codes[2]);
        using var newCode = await client.LoginMfaAsync(await client.LoginMfaTokenAsync("recovery@example.com"), recoveryCode: renewed[0]);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(again)).ShouldBe("invalid_mfa_code");
        other.StatusCode.ShouldBe(HttpStatusCode.OK);
        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(wrongPassword)).ShouldBe("invalid_password");
        regenerate.StatusCode.ShouldBe(HttpStatusCode.OK);
        renewed.Count.ShouldBe(10);
        renewed.ShouldNotContain(codes[2]);
        oldCode.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        newCode.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MfaDisable_RequiresThePasswordAndThenLoginReturnsTokensDirectly()
    {
        var (factory, clock) = await MfaTestHost.CreateAsync(postgres);
        await using var _ = factory;
        using var client = new AccountClient(factory);
        var (_, key, _) = await MfaTestHost.EnableMfaAsync(client, clock, "disable@example.com");
        using var signIn = await client.LoginMfaAsync(
            await client.LoginMfaTokenAsync("disable@example.com"), code: Rfc6238.Code(key, clock.GetUtcNow()));
        var session = (await signIn.Content.ReadDataAsync<TokenBody>(Ct))!;

        using var wrong = await client.MfaDisableAsync(session.AccessToken, "not the right password");
        using var disable = await client.MfaDisableAsync(session.AccessToken);
        var tokens = await client.LoginTokensAsync("disable@example.com");
        using var regenerate = await client.MfaRecoveryCodesAsync(tokens.AccessToken);
        using var setup = await client.MfaSetupAsync(tokens.AccessToken);

        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(wrong)).ShouldBe("invalid_password");
        disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        tokens.TokenType.ShouldBe("Bearer");
        new JsonWebToken(tokens.AccessToken).Claims.Where(c => c.Type == "amr").Select(c => c.Value).ShouldBe(["pwd"]);
        regenerate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AccountClient.ProblemCodeAsync(regenerate)).ShouldBe("mfa_not_enabled");
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await setup.Content.ReadDataAsync<MfaSetupBody>(Ct))!.SharedKey.ShouldNotBe(key);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MfaEndpoints_RevokedSession_Return401()
    {
        var (factory, _) = await MfaTestHost.CreateAsync(postgres);
        await using var _ = factory;
        using var client = new AccountClient(factory);
        await client.RegisterConfirmedAsync("revoked@example.com");
        var tokens = await client.LoginTokensAsync("revoked@example.com");
        using (var logoutAll = await client.LogoutAllAsync(tokens.AccessToken))
        {
            logoutAll.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var setup = await client.MfaSetupAsync(tokens.AccessToken);
        using var verify = await client.MfaVerifyAsync(tokens.AccessToken, "123456");
        using var disable = await client.MfaDisableAsync(tokens.AccessToken);
        using var codes = await client.MfaRecoveryCodesAsync(tokens.AccessToken);

        // D-29: the mfa endpoints are live-session endpoints.
        foreach (var response in new[] { setup, verify, disable, codes })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await AccountClient.ProblemCodeAsync(response)).ShouldBe("session_revoked");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MfaVerify_WrongCode_Returns400AndLeavesMfaDisabled()
    {
        var (factory, clock) = await MfaTestHost.CreateAsync(postgres);
        await using var _ = factory;
        using var client = new AccountClient(factory);
        await client.RegisterConfirmedAsync("verify@example.com");
        var tokens = await client.LoginTokensAsync("verify@example.com");
        using var noKey = await client.MfaVerifyAsync(tokens.AccessToken, "123456");
        using var setup = await client.MfaSetupAsync(tokens.AccessToken);
        var key = (await setup.Content.ReadDataAsync<MfaSetupBody>(Ct))!.SharedKey;

        using var wrong = await client.MfaVerifyAsync(tokens.AccessToken, Rfc6238.WrongCode(key, clock.GetUtcNow()));
        using var malformed = await client.MfaVerifyAsync(tokens.AccessToken, "12ab56");
        var login = await client.LoginTokensAsync("verify@example.com");

        noKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(noKey)).ShouldBe("invalid_code");
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(wrong)).ShouldBe("invalid_code");
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(malformed)).ShouldBe("validation_failed");
        login.AccessToken.ShouldNotBeNullOrEmpty();
    }
}
