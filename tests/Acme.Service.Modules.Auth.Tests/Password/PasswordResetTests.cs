using System.Net;
using System.Net.Http.Json;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Password;

/// <summary>
/// Forgot and reset password end to end (D-23, INV-15): the generic 202, the emailed one-time link (outbox, inbox
/// handler, job, notification sender), session revocation and lockout reset.
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class PasswordResetTests(PostgresFixture postgres)
{
    private const string NewPassword = "a brand new passphrase";

    private static readonly TimeSpan _emailTimeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ForgotPassword_UnknownOrUnconfirmedEmail_Returns202AndSendsNoEmail()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        await client.RegisterConfirmedAsync("known@example.com");
        using (var register = await client.RegisterAsync("unconfirmed@example.com"))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await factory.Emails.WaitForEmailAsync("unconfirmed@example.com", _emailTimeout, Ct);

        using var unknown = await client.ForgotPasswordAsync("nobody@example.com");
        using var unconfirmed = await client.ForgotPasswordAsync("unconfirmed@example.com");
        using var known = await client.ForgotPasswordAsync("known@example.com");

        // The known address's email proves the pipeline delivered everything enqueued before it.
        var reset = await factory.Emails.WaitForEmailAsync("known@example.com", _emailTimeout, Ct, count: 2);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        unconfirmed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        known.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var bodies = new[]
        {
            (await unknown.Content.ReadDataElementAsync(Ct)).GetRawText(),
            (await unconfirmed.Content.ReadDataElementAsync(Ct)).GetRawText(),
            (await known.Content.ReadDataElementAsync(Ct)).GetRawText(),
        };
        bodies.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem().ShouldContain("further instructions");
        reset.Subject.ShouldBe("Reset your password");
        factory.Emails.SentTo("nobody@example.com").ShouldBeEmpty();
        factory.Emails.SentTo("unconfirmed@example.com").Count.ShouldBe(1);
        (await AuthTestServices.ScalarAsync<long>(
            factory.ConnectionString,
            "SELECT count(*) FROM auth.outbox_messages WHERE type = 'auth.password-reset-requested.v1'")).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetPassword_WithEmailedLink_ChangesPasswordRevokesSessionsAndOldRefreshTokensFail()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        var userId = await client.RegisterConfirmedAsync("reset@example.com");
        var first = await client.LoginTokensAsync("reset@example.com");
        var second = await client.LoginTokensAsync("reset@example.com");
        var stampBefore = await SecurityStampAsync(factory, userId);

        using (var forgot = await client.ForgotPasswordAsync("reset@example.com"))
        {
            forgot.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var email = await factory.Emails.WaitForEmailAsync("reset@example.com", _emailTimeout, Ct, count: 2);
        var link = ConfirmationLink.From(email);
        using var reset = await client.ResetPasswordAsync(link.UserId, link.Token, NewPassword);
        using var refreshFirst = await client.RefreshAsync(first.RefreshToken);
        using var refreshSecond = await client.RefreshAsync(second.RefreshToken);
        using var oldPassword = await client.LoginAsync("reset@example.com");
        using var reuse = await client.ResetPasswordAsync(link.UserId, link.Token, "yet another passphrase");
        var signedIn = await client.LoginTokensAsync("reset@example.com", NewPassword);

        link.Uri.GetLeftPart(UriPartial.Path).ShouldBe("https://app.example.test/reset-password");
        link.UserId.ShouldBe(userId.ToString());
        email.TextBody.ShouldContain("expires in 1 hour");
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent, await reset.Content.ReadAsStringAsync(Ct));
        refreshFirst.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        refreshSecond.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        oldPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The security stamp changed, so the link works once.
        reuse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(reuse)).ShouldBe("invalid_token");
        (await SecurityStampAsync(factory, userId)).ShouldNotBe(stampBefore);
        signedIn.AccessToken.ShouldNotBeNullOrEmpty();
        (await AuthTestServices.RowsAsync(
            factory.ConnectionString,
            "SELECT DISTINCT revoked_reason FROM auth.sessions WHERE created_at < (SELECT max(created_at) FROM auth.sessions)"))
            .ShouldBe(["password_reset"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetPassword_InvalidTokenUnknownUserOrWeakPassword_Returns400()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        var userId = await client.RegisterConfirmedAsync("bad@example.com");
        using (var forgot = await client.ForgotPasswordAsync("bad@example.com"))
        {
            forgot.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var link = ConfirmationLink.From(await factory.Emails.WaitForEmailAsync("bad@example.com", _emailTimeout, Ct, count: 2));

        using var badToken = await client.ResetPasswordAsync(userId.ToString(), "not-a-token", NewPassword);
        using var unknownUser = await client.ResetPasswordAsync(Guid.CreateVersion7().ToString(), link.Token, NewPassword);
        using var weak = await client.ResetPasswordAsync(link.UserId, link.Token, "short");
        using var stillValid = await client.ResetPasswordAsync(link.UserId, link.Token, NewPassword);

        badToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(badToken)).ShouldBe("invalid_token");
        unknownUser.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(unknownUser)).ShouldBe("invalid_token");
        weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(weak)).ShouldBe("validation_failed");
        (await weak.Content.ReadAsStringAsync(Ct)).ShouldContain("newPassword");

        // A rejected new password does not use the link up.
        stillValid.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetPassword_LockedAccount_ClearsTheLockout()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        var userId = await client.RegisterConfirmedAsync("unlock@example.com");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await client.LoginAsync("unlock@example.com", "not the right password");
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var forgot = await client.ForgotPasswordAsync("unlock@example.com"))
        {
            forgot.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var link = ConfirmationLink.From(await factory.Emails.WaitForEmailAsync("unlock@example.com", _emailTimeout, Ct, count: 2));
        using var reset = await client.ResetPasswordAsync(link.UserId, link.Token, NewPassword);
        using var login = await client.LoginAsync("unlock@example.com", NewPassword);

        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AuthTestServices.RowsAsync(
            factory.ConnectionString,
            $"SELECT access_failed_count, lockout_end IS NULL FROM auth.users WHERE id = '{userId}'")).ShouldBe(["0|True"]);
    }

    private async Task<AccountWebFactory> CreateHostAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        return new AccountWebFactory(connectionString);
    }

    private static Task<string?> SecurityStampAsync(AccountWebFactory factory, Guid userId) =>
        AuthTestServices.ScalarAsync<string>(factory.ConnectionString, "SELECT security_stamp FROM auth.users WHERE id = @id", ("id", userId));
}
