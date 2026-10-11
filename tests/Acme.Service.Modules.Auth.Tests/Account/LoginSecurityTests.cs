using System.Net;
using System.Net.Http.Json;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Account;

/// <summary>
/// Sign-in failures (D-23) and session revocation (D-29): generic 401s, lockout after 5 failures even for the right
/// password, and logout-all revoking every session and failing the live-session check of the old access token.
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class LoginSecurityTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Login_FiveWrongPasswords_LocksAccountAndCorrectPasswordThenReturns401()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        var userId = await client.RegisterConfirmedAsync("lock@example.com");

        var failures = new List<string?>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await client.LoginAsync("lock@example.com", "not the right password");
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            failures.Add(await AccountClient.ProblemCodeAsync(wrong));
        }

        using var correct = await client.LoginAsync("lock@example.com");

        failures.ShouldAllBe(code => code == "invalid_credentials");
        correct.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(correct)).ShouldBe("invalid_credentials");
        var lockoutEnd = await AuthTestServices.ScalarAsync<DateTime>(
            factory.ConnectionString,
            "SELECT lockout_end FROM auth.users WHERE id = @id",
            ("id", userId));
        lockoutEnd.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(10));
        (await AuthTestServices.ScalarAsync<long>(factory.ConnectionString, "SELECT count(*) FROM auth.sessions")).ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Login_UnknownEmailAndWrongPassword_ReturnIdenticalProblems()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        await client.RegisterConfirmedAsync("known@example.com");

        using var unknown = await client.LoginAsync("unknown@example.com");
        using var wrong = await client.LoginAsync("known@example.com", "not the right password");

        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var unknownBody = (await unknown.Content.ReadFromJsonAsync<Problem>(Ct))!;
        var wrongBody = (await wrong.Content.ReadFromJsonAsync<Problem>(Ct))!;
        unknownBody.Code.ShouldBe("invalid_credentials");
        unknownBody.ShouldBe(wrongBody with { TraceId = unknownBody.TraceId, CorrelationId = unknownBody.CorrelationId });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LogoutAll_TwoSessions_RevokesBothAndOldAccessTokenFailsLiveSession()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        var userId = await client.RegisterConfirmedAsync("all@example.com");
        var first = await client.LoginTokensAsync("all@example.com");
        var second = await client.LoginTokensAsync("all@example.com");
        var stampBefore = await SecurityStampAsync(factory, userId);

        using var logoutAll = await client.LogoutAllAsync(first.AccessToken);
        using var refreshFirst = await client.RefreshAsync(first.RefreshToken);
        using var refreshSecond = await client.RefreshAsync(second.RefreshToken);
        using var again = await client.LogoutAllAsync(first.AccessToken);
        using var me = await client.MeAsync(first.AccessToken);

        logoutAll.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        refreshFirst.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        refreshSecond.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        again.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(again)).ShouldBe("session_revoked");
        (await SecurityStampAsync(factory, userId)).ShouldNotBe(stampBefore);
        (await AuthTestServices.RowsAsync(factory.ConnectionString, "SELECT DISTINCT revoked_reason FROM auth.sessions"))
            .ShouldBe(["logout_all"]);

        // D-29: access tokens are stateless; endpoints without the live-session policy accept them until they expire.
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Logout_AnotherUsersRefreshToken_Returns204AndKeepsTheirSession()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        await client.RegisterConfirmedAsync("alice@example.com");
        await client.RegisterConfirmedAsync("bob@example.com");
        var alice = await client.LoginTokensAsync("alice@example.com");
        var bob = await client.LoginTokensAsync("bob@example.com");

        using var logout = await client.LogoutAsync(alice.AccessToken, bob.RefreshToken);
        using var bobRefresh = await client.RefreshAsync(bob.RefreshToken);
        using var anonymous = await client.LogoutAsync(null, alice.RefreshToken);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        bobRefresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<AccountWebFactory> CreateHostAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        return new AccountWebFactory(connectionString);
    }

    private static Task<string?> SecurityStampAsync(AccountWebFactory factory, Guid userId) =>
        AuthTestServices.ScalarAsync<string>(factory.ConnectionString, "SELECT security_stamp FROM auth.users WHERE id = @id", ("id", userId));

    private sealed record Problem(int Status, string? Title, string? Detail, string? Code, string? TraceId, string? CorrelationId);
}
