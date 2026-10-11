using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Acme.Service.BuildingBlocks.Notifications;
using Microsoft.AspNetCore.WebUtilities;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>The account API as the tests call it.</summary>
internal sealed class AccountClient(AccountWebFactory factory, string? clientIp = null) : IDisposable
{
    public const string Prefix = "/api/v1/auth";

    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Task<HttpResponseMessage> RegisterAsync(string email, string password = TestUsers.Password, string? displayName = null) =>
        PostAsync("/register", new { email, password, displayName });

    public Task<HttpResponseMessage> ConfirmEmailAsync(string userId, string token) =>
        PostAsync("/confirm-email", new { userId, token });

    public Task<HttpResponseMessage> ResendConfirmationAsync(string email) =>
        PostAsync("/resend-confirmation", new { email });

    public Task<HttpResponseMessage> LoginAsync(string email, string password = TestUsers.Password) =>
        PostAsync("/login", new { email, password });

    public Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        PostAsync("/refresh", new { refreshToken });

    public Task<HttpResponseMessage> LogoutAsync(string? accessToken, string refreshToken) =>
        PostAsync("/logout", new { refreshToken }, accessToken);

    public Task<HttpResponseMessage> LogoutAllAsync(string accessToken) =>
        PostAsync("/logout-all", null, accessToken);

    public Task<HttpResponseMessage> LoginMfaAsync(string mfaToken, string? code = null, string? recoveryCode = null) =>
        PostAsync("/login/mfa", new { mfaToken, code, recoveryCode });

    public Task<HttpResponseMessage> ChangePasswordAsync(string accessToken, string currentPassword, string newPassword) =>
        PostAsync("/change-password", new { currentPassword, newPassword }, accessToken);

    public Task<HttpResponseMessage> ForgotPasswordAsync(string email) =>
        PostAsync("/forgot-password", new { email });

    public Task<HttpResponseMessage> ResetPasswordAsync(string userId, string token, string newPassword) =>
        PostAsync("/reset-password", new { userId, token, newPassword });

    public Task<HttpResponseMessage> MfaSetupAsync(string accessToken) =>
        PostAsync("/mfa/authenticator", null, accessToken);

    public Task<HttpResponseMessage> MfaVerifyAsync(string accessToken, string code) =>
        PostAsync("/mfa/authenticator/verify", new { code }, accessToken);

    public Task<HttpResponseMessage> MfaDisableAsync(string accessToken, string password = TestUsers.Password) =>
        PostAsync("/mfa/disable", new { password }, accessToken);

    public Task<HttpResponseMessage> MfaRecoveryCodesAsync(string accessToken, string password = TestUsers.Password) =>
        PostAsync("/mfa/recovery-codes", new { password }, accessToken);

    /// <summary>Logs in an account with two-factor authentication and returns the mfa token, asserting 200 and no tokens.</summary>
    public async Task<string> LoginMfaTokenAsync(string email, string password = TestUsers.Password)
    {
        using var response = await LoginAsync(email, password);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        document.RootElement.TryGetProperty("accessToken", out _).ShouldBeFalse();
        return document.RootElement.GetProperty("mfaToken").GetString()!;
    }

    public async Task<HttpResponseMessage> MeAsync(string? accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Prefix + "/me", UriKind.Relative));
        Authorize(request, accessToken);
        return await _client.SendAsync(request, Ct);
    }

    /// <summary>Logs in and returns the tokens, asserting 200.</summary>
    public async Task<TokenBody> LoginTokensAsync(string email, string password = TestUsers.Password)
    {
        using var response = await LoginAsync(email, password);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TokenBody>(Ct))!;
    }

    /// <summary>Registers, reads the captured confirmation email and confirms the address; returns the user id.</summary>
    public async Task<Guid> RegisterConfirmedAsync(string email)
    {
        using (var register = await RegisterAsync(email))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var link = ConfirmationLink.From(await factory.Emails.WaitForEmailAsync(email, TimeSpan.FromSeconds(30), Ct));
        using var confirm = await ConfirmEmailAsync(link.UserId, link.Token);
        confirm.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return Guid.Parse(link.UserId);
    }

    public void Dispose() => _client.Dispose();

    /// <summary>The <c>code</c> extension of a problem response.</summary>
    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object? body, string? accessToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Prefix + path, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        Authorize(request, accessToken);
        return await _client.SendAsync(request, Ct);
    }

    private void Authorize(HttpRequestMessage request, string? accessToken)
    {
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (clientIp is not null)
        {
            request.Headers.Add(AccountWebFactory.ClientIpHeader, clientIp);
        }
    }
}

/// <summary>The token response of login and refresh.</summary>
internal sealed record TokenBody(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string TokenType);

/// <summary>The body of <c>POST /mfa/authenticator</c>.</summary>
internal sealed record MfaSetupBody(string SharedKey, string AuthenticatorUri);

/// <summary>The body of the endpoints returning recovery codes.</summary>
internal sealed record RecoveryCodesBody(IReadOnlyList<string> RecoveryCodes);

/// <summary>The link of a captured confirmation or password reset email.</summary>
internal sealed record ConfirmationLink(Uri Uri, string UserId, string Token)
{
    public static ConfirmationLink From(EmailMessage message)
    {
        var line = message.TextBody.Split('\n').Single(l => l.StartsWith("http", StringComparison.Ordinal));
        var uri = new Uri(line.Trim());
        var query = QueryHelpers.ParseQuery(uri.Query);
        return new ConfirmationLink(uri, query["userId"].ToString(), query["token"].ToString());
    }
}
