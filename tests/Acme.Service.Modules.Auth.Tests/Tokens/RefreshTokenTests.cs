using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Sessions;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Tokens;

/// <summary>
/// Sessions and refresh tokens against a migrated database (D-11, D-29, INV-15): rotation, reuse detection revoking the
/// whole session, the single-use race, the live-session policy and permissions computed at issue time (D-09).
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class RefreshTokenTests(PostgresFixture postgres)
{
    private static readonly string[] _password = ["pwd"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshAsync_ValidToken_RotatesWithinSessionAndStoresOnlyHashes()
    {
        await using var factory = await CreateHostAsync();
        var userId = await CreateUserAsync(factory, "rotate@example.com", AuthRole.Staff);
        var first = await CreateSessionAsync(factory, userId);

        var second = (await RefreshAsync(factory, first.RefreshToken)).Value;

        second.SessionId.ShouldBe(first.SessionId);
        second.RefreshToken.ShouldNotBe(first.RefreshToken);
        second.AccessToken.ShouldNotBe(first.AccessToken);
        var rows = await AuthTestServices.RowsAsync(
            factory.ConnectionString,
            $"SELECT id, used_at IS NOT NULL, replaced_by_id, length(token_hash) FROM auth.refresh_tokens WHERE session_id = '{first.SessionId}' ORDER BY created_at, id");
        rows.Count.ShouldBe(2);
        var oldRow = rows[0].Split('|');
        var newRow = rows[1].Split('|');
        oldRow[1].ShouldBe("True");
        oldRow[2].ShouldBe(newRow[0]);
        newRow[1].ShouldBe("False");
        newRow[2].ShouldBe("<null>");
        oldRow[3].ShouldBe("32");
        foreach (var token in new[] { first.RefreshToken, second.RefreshToken })
        {
            var raw = Base64Url.DecodeFromChars(token);
            (await AuthTestServices.ScalarAsync<long>(
                factory.ConnectionString,
                "SELECT count(*) FROM auth.refresh_tokens WHERE token_hash = @raw OR token_hash = @hash",
                ("raw", raw),
                ("hash", SHA256.HashData(raw)))).ShouldBe(1);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshAsync_RoleGrantedAfterSignIn_IssuesFreshPermissions()
    {
        await using var factory = await CreateHostAsync();
        var userId = await CreateUserAsync(factory, "fresh@example.com", AuthRole.Customer);
        var first = await CreateSessionAsync(factory, userId);

        using var before = await ProbeClient.GetAsync(factory, ProbeModule.PermissionPath, first.AccessToken);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            (await users.AddToRoleAsync((await users.FindByIdAsync(userId.ToString()))!, AuthRole.Staff)).Succeeded.ShouldBeTrue();
        }

        var second = (await RefreshAsync(factory, first.RefreshToken)).Value;
        using var after = await ProbeClient.GetAsync(factory, ProbeModule.PermissionPath, second.AccessToken);
        using var claims = await ProbeClient.GetAsync(factory, ProbeModule.AuthenticatedPath, second.AccessToken);

        before.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        var probe = (await claims.Content.ReadFromJsonAsync<ProbeClaims>(Ct))!;
        probe.Sub.ShouldBe(userId.ToString());
        probe.Sid.ShouldBe(first.SessionId.ToString());
        probe.Email.ShouldBe("fresh@example.com");
        probe.Roles.ShouldBe([AuthRole.Customer, AuthRole.Staff], ignoreOrder: true);
        probe.Perms.ShouldContain("users.read");
        probe.Perms.ShouldContain("roles.read");
        probe.Amr.ShouldBe(_password);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshAsync_ReusedToken_Returns401AndRevokesWholeSession()
    {
        await using var factory = await CreateHostAsync();
        var userId = await CreateUserAsync(factory, "reuse@example.com", AuthRole.Customer);
        var first = await CreateSessionAsync(factory, userId);
        var second = (await RefreshAsync(factory, first.RefreshToken)).Value;

        var reuse = await RefreshAsync(factory, first.RefreshToken);
        var newer = await RefreshAsync(factory, second.RefreshToken);
        using var live = await ProbeClient.GetAsync(factory, ProbeModule.LiveSessionPath, second.AccessToken);

        reuse.IsFailure.ShouldBeTrue();
        reuse.Error.Code.ShouldBe("invalid_refresh_token");
        newer.IsFailure.ShouldBeTrue();
        newer.Error.Code.ShouldBe("invalid_refresh_token");
        (await SessionRevocationAsync(factory, first.SessionId)).ShouldBe("reuse_detected");
        live.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProbeClient.ProblemCodeAsync(live)).ShouldBe(LiveSessionResultHandler.SessionRevokedCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshAsync_TwoParallelUsesOfSameToken_ExactlyOneSucceeds()
    {
        await using var factory = await CreateHostAsync();
        var userId = await CreateUserAsync(factory, "race@example.com", AuthRole.Customer);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var pair = await CreateSessionAsync(factory, userId);
            using var start = new ManualResetEventSlim(false);
            var racers = Enumerable.Range(0, 2).Select(_ => Task.Run(
                async () =>
                {
                    start.Wait(Ct);
                    return await RefreshAsync(factory, pair.RefreshToken);
                },
                Ct)).ToArray();
            start.Set();
            var results = await Task.WhenAll(racers);

            results.Count(r => r.IsSuccess).ShouldBe(1);
            results.Single(r => r.IsFailure).Error.Code.ShouldBe("invalid_refresh_token");

            // The loser presented a used token: the whole family is revoked (D-11).
            (await SessionRevocationAsync(factory, pair.SessionId)).ShouldBe("reuse_detected");
            (await RefreshAsync(factory, results.Single(r => r.IsSuccess).Value.RefreshToken)).IsFailure.ShouldBeTrue();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveSessionPolicy_SessionRevoked_Returns401WhileStatelessEndpointsStillAccept()
    {
        await using var factory = await CreateHostAsync();
        var userId = await CreateUserAsync(factory, "live@example.com", AuthRole.Customer);
        var pair = await CreateSessionAsync(factory, userId);

        using var liveBefore = await ProbeClient.GetAsync(factory, ProbeModule.LiveSessionPath, pair.AccessToken);
        var revoked = await ProbeClient.WithRefreshServiceAsync(factory, s => s.RevokeSessionAsync(pair.SessionId, userId, SessionRevocationReasons.Logout, Ct));
        using var liveAfter = await ProbeClient.GetAsync(factory, ProbeModule.LiveSessionPath, pair.AccessToken);
        using var stateless = await ProbeClient.GetAsync(factory, ProbeModule.AuthenticatedPath, pair.AccessToken);

        liveBefore.StatusCode.ShouldBe(HttpStatusCode.OK);
        revoked.Value.ShouldBeTrue();
        liveAfter.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        liveAfter.Headers.WwwAuthenticate.ShouldContain(h => h.Scheme == "Bearer");
        (await ProbeClient.ProblemCodeAsync(liveAfter)).ShouldBe("session_revoked");

        // D-29: access tokens are stateless; without the live-session policy they stay valid until they expire.
        stateless.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(factory, pair.RefreshToken)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveSessionPolicy_SessionOfAnotherUser_Returns401()
    {
        await using var factory = await CreateHostAsync();
        var owner = await CreateUserAsync(factory, "owner@example.com", AuthRole.Customer);
        var pair = await CreateSessionAsync(factory, owner);

        // A token for another subject that claims the owner's live session id.
        var forged = ProbeClient.IssueAccessToken(factory, pair.SessionId);
        using var response = await ProbeClient.GetAsync(factory, ProbeModule.LiveSessionPath, forged);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RevokeAllSessionsAsync_UserWithSessions_RevokesEveryRefreshFamily()
    {
        await using var factory = await CreateHostAsync();
        var userId = await CreateUserAsync(factory, "all@example.com", AuthRole.Customer);
        var a = await CreateSessionAsync(factory, userId);
        var b = await CreateSessionAsync(factory, userId);

        var count = await ProbeClient.WithRefreshServiceAsync(factory, s => s.RevokeAllSessionsAsync(userId, SessionRevocationReasons.LogoutAll, Ct));

        count.Value.ShouldBe(2);
        (await RefreshAsync(factory, a.RefreshToken)).IsFailure.ShouldBeTrue();
        (await RefreshAsync(factory, b.RefreshToken)).IsFailure.ShouldBeTrue();
        (await SessionRevocationAsync(factory, a.SessionId)).ShouldBe("logout_all");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RevokeSessionByRefreshTokenAsync_OwnToken_RevokesOnlyThatSession()
    {
        await using var factory = await CreateHostAsync();
        var userId = await CreateUserAsync(factory, "logout@example.com", AuthRole.Customer);
        var otherId = await CreateUserAsync(factory, "other@example.com", AuthRole.Customer);
        var kept = await CreateSessionAsync(factory, userId);
        var ended = await CreateSessionAsync(factory, userId);

        var byOther = await ProbeClient.WithRefreshServiceAsync(factory, s => s.RevokeSessionByRefreshTokenAsync(ended.RefreshToken, otherId, SessionRevocationReasons.Logout, Ct));
        var malformed = await ProbeClient.WithRefreshServiceAsync(factory, s => s.RevokeSessionByRefreshTokenAsync("not-a-token", userId, SessionRevocationReasons.Logout, Ct));
        var byOwner = await ProbeClient.WithRefreshServiceAsync(factory, s => s.RevokeSessionByRefreshTokenAsync(ended.RefreshToken, userId, SessionRevocationReasons.Logout, Ct));

        byOther.Value.ShouldBeFalse();
        malformed.Value.ShouldBeFalse();
        byOwner.Value.ShouldBeTrue();
        (await SessionRevocationAsync(factory, ended.SessionId)).ShouldBe("logout");
        (await SessionRevocationAsync(factory, kept.SessionId)).ShouldBeNull();
        (await RefreshAsync(factory, ended.RefreshToken)).IsFailure.ShouldBeTrue();
        (await RefreshAsync(factory, kept.RefreshToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshAsync_LockedUser_Returns401AndRevokesSession()
    {
        await using var factory = await CreateHostAsync();
        var userId = await CreateUserAsync(factory, "locked@example.com", AuthRole.Customer);
        var pair = await CreateSessionAsync(factory, userId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            await users.SetLockoutEndDateAsync((await users.FindByIdAsync(userId.ToString()))!, DateTimeOffset.UtcNow.AddHours(1));
        }

        var result = await RefreshAsync(factory, pair.RefreshToken);

        result.IsFailure.ShouldBeTrue();
        (await SessionRevocationAsync(factory, pair.SessionId)).ShouldBe("user_inactive");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshAsync_UnconfirmedEmail_Returns401()
    {
        await using var factory = await CreateHostAsync();
        var userId = await TestUsers.CreateAsync(factory.Services, "unconfirmed@example.com");
        var pair = await CreateSessionAsync(factory, userId);

        (await RefreshAsync(factory, pair.RefreshToken)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshAsync_UnknownToken_Returns401WithoutChanges()
    {
        await using var factory = await CreateHostAsync();

        var result = await RefreshAsync(factory, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("invalid_refresh_token");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RefreshAsync_TokenPastLifetime_Returns401()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = await CreateHostAsync(clock);
        var userId = await CreateUserAsync(factory, "expired@example.com", AuthRole.Customer);
        var pair = await CreateSessionAsync(factory, userId);

        clock.Advance(TimeSpan.FromDays(14) + TimeSpan.FromSeconds(1));
        var result = await RefreshAsync(factory, pair.RefreshToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("invalid_refresh_token");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateSessionAsync_SessionShorterThanRefreshLifetime_CapsTokenExpiryAtSessionEnd()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = await CreateHostAsync(clock, new Dictionary<string, string?> { ["Auth:Jwt:SessionAbsoluteLifetime"] = "1.00:00:00" });
        var userId = await CreateUserAsync(factory, "cap@example.com", AuthRole.Customer);

        var pair = await CreateSessionAsync(factory, userId);
        clock.Advance(TimeSpan.FromHours(23));
        var renewed = (await RefreshAsync(factory, pair.RefreshToken)).Value;
        clock.Advance(TimeSpan.FromHours(1));
        var afterSessionEnd = await RefreshAsync(factory, renewed.RefreshToken);

        pair.RefreshTokenExpiresAt.ShouldBe(clock.GetUtcNow() - TimeSpan.FromHours(24) + TimeSpan.FromDays(1));
        renewed.RefreshTokenExpiresAt.ShouldBe(pair.RefreshTokenExpiresAt);
        afterSessionEnd.IsFailure.ShouldBeTrue();
    }

    private async Task<ProbeWebFactory> CreateHostAsync(TimeProvider? clock = null, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        return new ProbeWebFactory(connectionString, overrides, clock);
    }

    private static async Task<Guid> CreateUserAsync(ProbeWebFactory factory, string email, string role)
    {
        var userId = await TestUsers.CreateAsync(factory.Services, email);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var user = (await users.FindByIdAsync(userId.ToString()))!;
        user.EmailConfirmed = true;
        (await users.UpdateAsync(user)).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        return userId;
    }

    private static async Task<TokenPair> CreateSessionAsync(ProbeWebFactory factory, Guid userId)
    {
        var result = await ProbeClient.WithRefreshServiceAsync(
            factory,
            s => s.CreateSessionAsync(userId, _password, new ClientInfo("203.0.113.7", "tests"), Ct));
        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }

    private static Task<BuildingBlocks.Results.Result<TokenPair>> RefreshAsync(ProbeWebFactory factory, string refreshToken) =>
        ProbeClient.WithRefreshServiceAsync(factory, s => s.RefreshAsync(refreshToken, new ClientInfo("203.0.113.7", "tests"), Ct));

    private static Task<string?> SessionRevocationAsync(ProbeWebFactory factory, Guid sessionId) =>
        AuthTestServices.ScalarAsync<string>(factory.ConnectionString, "SELECT revoked_reason FROM auth.sessions WHERE id = @id", ("id", sessionId));
}
