using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Account;

/// <summary>
/// The account endpoints end to end against a migrated database: register, the emailed confirmation link (outbox,
/// inbox handler, job, notification sender), confirm, login, me, refresh and logout, plus the enumeration-resistant
/// answers of D-23.
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class AccountFlowTests(PostgresFixture postgres)
{
    private static readonly TimeSpan _emailTimeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Account_RegisterConfirmLoginMeRefreshLogout_FullFlowSucceeds()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);

        using var register = await client.RegisterAsync("Flow@Example.com", displayName: "  Flow User ");
        var email = await factory.Emails.WaitForEmailAsync("flow@example.com", _emailTimeout, Ct);
        var link = ConfirmationLink.From(email);
        using var confirm = await client.ConfirmEmailAsync(link.UserId, link.Token);
        var tokens = await client.LoginTokensAsync("flow@example.com");
        using var me = await client.MeAsync(tokens.AccessToken);
        using var refresh = await client.RefreshAsync(tokens.RefreshToken);
        var renewed = (await refresh.Content.ReadFromJsonAsync<TokenBody>(Ct))!;
        using var logout = await client.LogoutAsync(renewed.AccessToken, renewed.RefreshToken);
        using var refreshAfterLogout = await client.RefreshAsync(renewed.RefreshToken);

        register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await register.Content.ReadAsStringAsync(Ct)).ShouldContain("further instructions");
        email.Subject.ShouldBe("Confirm your email address");
        link.Uri.GetLeftPart(UriPartial.Path).ShouldBe("https://app.example.test/confirm-email");
        email.HtmlBody.ShouldNotBeNull().ShouldContain("Confirm email address");
        confirm.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        tokens.TokenType.ShouldBe("Bearer");
        tokens.AccessTokenExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        tokens.RefreshTokenExpiresAt.ShouldBeGreaterThan(tokens.AccessTokenExpiresAt);

        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var account = (await me.Content.ReadFromJsonAsync<MeBody>(Ct))!;
        account.Id.ToString().ShouldBe(link.UserId);
        account.Email.ShouldBe("Flow@Example.com");
        account.DisplayName.ShouldBe("Flow User");
        account.EmailConfirmed.ShouldBeTrue();
        account.MfaEnabled.ShouldBeFalse();
        account.Roles.ShouldBe([AuthRole.Customer]);
        account.Permissions.ShouldBe(
        [
            // <sample:orders>
            "orders.cancel", "orders.create", "orders.read",
            // </sample:orders>
        ]);

        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        renewed.RefreshToken.ShouldNotBe(tokens.RefreshToken);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        refreshAfterLogout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(refreshAfterLogout)).ShouldBe("invalid_refresh_token");
        (await AuthTestServices.ScalarAsync<string>(
            factory.ConnectionString,
            "SELECT revoked_reason FROM auth.sessions WHERE user_id = @id",
            ("id", account.Id))).ShouldBe("logout");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_ExistingEmail_Returns202AndCreatesNoSecondUser()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);

        using var first = await client.RegisterAsync("dup@example.com");
        using var second = await client.RegisterAsync("DUP@example.com", "another long password");

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await second.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
        (await AuthTestServices.ScalarAsync<long>(
            factory.ConnectionString,
            "SELECT count(*) FROM auth.users WHERE normalized_email = 'DUP@EXAMPLE.COM'")).ShouldBe(1);
        (await OutboxCountAsync(factory, "auth.user-registered.v1")).ShouldBe(1);
        (await OutboxCountAsync(factory, "auth.email-verification-requested.v1")).ShouldBe(1);
        using var login = await client.LoginAsync("dup@example.com", "another long password");
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_NewUser_WritesUserRegisteredWithDefaultRole()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);

        using var register = await client.RegisterAsync("new@example.com", displayName: "New");

        register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var payload = await AuthTestServices.ScalarAsync<string>(
            factory.ConnectionString,
            "SELECT payload FROM auth.outbox_messages WHERE type = 'auth.user-registered.v1'");
        using (var registered = JsonDocument.Parse(payload.ShouldNotBeNull()))
        {
            registered.RootElement.GetProperty("email").GetString().ShouldBe("new@example.com");
            registered.RootElement.GetProperty("displayName").GetString().ShouldBe("New");
        }
        var verification = await AuthTestServices.ScalarAsync<string>(
            factory.ConnectionString,
            "SELECT payload FROM auth.outbox_messages WHERE type = 'auth.email-verification-requested.v1'");
        verification.ShouldNotBeNull().ShouldNotContain("token", Case.Insensitive);
        (await AuthTestServices.ScalarAsync<string>(
            factory.ConnectionString,
            "SELECT r.name FROM auth.user_roles ur JOIN auth.roles r ON r.id = ur.role_id JOIN auth.users u ON u.id = ur.user_id WHERE u.normalized_email = 'NEW@EXAMPLE.COM'"))
            .ShouldBe(AuthRole.Customer);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_ShortPassword_Returns400ValidationProblem()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);

        using var response = await client.RegisterAsync("short@example.com", "too short");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await AccountClient.ProblemCodeAsync(response)).ShouldBe("validation_failed");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("too short");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Login_UnconfirmedEmailWithCorrectPassword_Returns403EmailNotVerified()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        using (var register = await client.RegisterAsync("unverified@example.com"))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        using var correct = await client.LoginAsync("unverified@example.com");
        using var wrong = await client.LoginAsync("unverified@example.com", "a wrong password!");

        correct.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AccountClient.ProblemCodeAsync(correct)).ShouldBe("email_not_verified");
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AccountClient.ProblemCodeAsync(wrong)).ShouldBe("invalid_credentials");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConfirmEmail_InvalidTokenOrUnknownUser_Returns400InvalidToken()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        using (var register = await client.RegisterAsync("confirm@example.com"))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var link = ConfirmationLink.From(await factory.Emails.WaitForEmailAsync("confirm@example.com", _emailTimeout, Ct));

        using var badToken = await client.ConfirmEmailAsync(link.UserId, "not-a-token");
        using var unknownUser = await client.ConfirmEmailAsync(Guid.CreateVersion7().ToString(), link.Token);

        badToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(badToken)).ShouldBe("invalid_token");
        unknownUser.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AccountClient.ProblemCodeAsync(unknownUser)).ShouldBe("invalid_token");
        (await AuthTestServices.ScalarAsync<bool>(
            factory.ConnectionString,
            "SELECT email_confirmed FROM auth.users WHERE id = @id",
            ("id", Guid.Parse(link.UserId)))).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResendConfirmation_UnconfirmedKnownAndUnknownEmails_Return202AndEmailOnlyTheAccount()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);
        using (var register = await client.RegisterAsync("resend@example.com"))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var first = ConfirmationLink.From(await factory.Emails.WaitForEmailAsync("resend@example.com", _emailTimeout, Ct));

        using var known = await client.ResendConfirmationAsync("resend@example.com");
        using var unknown = await client.ResendConfirmationAsync("nobody@example.com");
        var second = ConfirmationLink.From(await factory.Emails.WaitForEmailAsync("resend@example.com", _emailTimeout, Ct, count: 2));

        known.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await unknown.Content.ReadAsStringAsync(Ct)).ShouldBe(await known.Content.ReadAsStringAsync(Ct));
        second.UserId.ShouldBe(first.UserId);
        factory.Emails.SentTo("nobody@example.com").ShouldBeEmpty();
        (await OutboxCountAsync(factory, "auth.email-verification-requested.v1")).ShouldBe(2);

        // Both links work until the address is confirmed.
        using var confirm = await client.ConfirmEmailAsync(second.UserId, second.Token);
        confirm.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Me_WithoutToken_Returns401()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory);

        using var response = await client.MeAsync(null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<AccountWebFactory> CreateHostAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);
        return new AccountWebFactory(connectionString);
    }

    private static async Task<long> OutboxCountAsync(AccountWebFactory factory, string type) =>
        await AuthTestServices.ScalarAsync<long>(
            factory.ConnectionString,
            "SELECT count(*) FROM auth.outbox_messages WHERE type = @type",
            ("type", type));

    private sealed record MeBody(Guid Id, string Email, string? DisplayName, bool EmailConfirmed, bool MfaEnabled, string[] Roles, string[] Permissions);
}
