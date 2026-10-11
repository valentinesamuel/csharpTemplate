using System.Net;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Password;

/// <summary>Change password (D-29): the current session survives, every other one ends, wrong passwords count.</summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class ChangePasswordTests(PostgresFixture postgres)
{
    private const string NewPassword = "a brand new passphrase";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ChangePassword_KeepsTheCurrentSessionAndRevokesTheOthers()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        var userId = await client.RegisterConfirmedAsync("change@example.com");
        var current = await client.LoginTokensAsync("change@example.com");
        var other = await client.LoginTokensAsync("change@example.com");
        var stampBefore = await AuthTestServices.ScalarAsync<string>(
            factory.ConnectionString, "SELECT security_stamp FROM auth.users WHERE id = @id", ("id", userId));

        using var change = await client.ChangePasswordAsync(current.AccessToken, TestUsers.Password, NewPassword);
        using var refreshCurrent = await client.RefreshAsync(current.RefreshToken);
        using var refreshOther = await client.RefreshAsync(other.RefreshToken);
        using var otherLiveSession = await client.LogoutAllAsync(other.AccessToken);
        using var oldPassword = await client.LoginAsync("change@example.com");
        using var newPassword = await client.LoginAsync("change@example.com", NewPassword);

        change.StatusCode.ShouldBe(HttpStatusCode.NoContent, await change.Content.ReadAsStringAsync(Ct));
        refreshCurrent.StatusCode.ShouldBe(HttpStatusCode.OK);
        refreshOther.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        otherLiveSession.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(otherLiveSession)).ShouldBe("session_revoked");
        oldPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        newPassword.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AuthTestServices.ScalarAsync<string>(
            factory.ConnectionString, "SELECT security_stamp FROM auth.users WHERE id = @id", ("id", userId))).ShouldNotBe(stampBefore);
        (await AuthTestServices.RowsAsync(factory.ConnectionString, "SELECT revoked_reason FROM auth.sessions ORDER BY created_at"))
            .ShouldBe(["<null>", "password_changed", "<null>"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ChangePassword_WrongCurrentPasswordOrWeakNewOne_Returns400AndWrongOnesCount()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        var userId = await client.RegisterConfirmedAsync("wrong@example.com");
        var tokens = await client.LoginTokensAsync("wrong@example.com");

        using var wrong = await client.ChangePasswordAsync(tokens.AccessToken, "not the right password", NewPassword);
        using var weak = await client.ChangePasswordAsync(tokens.AccessToken, TestUsers.Password, "short");
        using var anonymous = await client.ChangePasswordAsync(null!, TestUsers.Password, NewPassword);

        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(wrong)).ShouldBe("invalid_password");
        weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(weak)).ShouldBe("validation_failed");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The wrong password counted towards lockout; the correct one (no 2FA) reset the counter before the weak password failed.
        (await AuthTestServices.ScalarAsync<int>(
            factory.ConnectionString, "SELECT access_failed_count FROM auth.users WHERE id = @id", ("id", userId))).ShouldBe(0);
        (await client.LoginTokensAsync("wrong@example.com")).AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ChangePassword_FiveWrongCurrentPasswords_LockTheAccount()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        await client.RegisterConfirmedAsync("guess@example.com");
        var tokens = await client.LoginTokensAsync("guess@example.com");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await client.ChangePasswordAsync(tokens.AccessToken, "not the right password", NewPassword);
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var correct = await client.ChangePasswordAsync(tokens.AccessToken, TestUsers.Password, NewPassword);
        using var login = await client.LoginAsync("guess@example.com");

        correct.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(correct)).ShouldBe("invalid_password");
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<AccountWebFactory> CreateHostAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        return new AccountWebFactory(connectionString);
    }
}
