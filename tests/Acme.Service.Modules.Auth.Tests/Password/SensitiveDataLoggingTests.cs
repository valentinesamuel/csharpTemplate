using System.Net;
using System.Net.Http.Json;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Password;

/// <summary>
/// INV-28: running the password and MFA flows logs none of the secrets they handle (passwords, emailed tokens and
/// links, access, refresh and mfa tokens, the authenticator key, recovery codes). Every log entry of the host, at every
/// level, is captured.
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class SensitiveDataLoggingTests(PostgresFixture postgres)
{
    private const string NewPassword = "a brand new passphrase";

    private static readonly TimeSpan _emailTimeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PasswordAndMfaFlows_LogNoSecrets()
    {
        using var recorder = new RecordingLoggerProvider();
        var (factory, clock) = await MfaTestHost.CreateAsync(postgres, configureServices: services =>
            services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(_ =>
                LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(recorder)))));
        await using var host = factory;
        using var client = new AccountClient(factory);
        var secrets = new List<string> { TestUsers.Password, NewPassword };

        await client.RegisterConfirmedAsync("secret@example.com");
        secrets.Add(ConfirmationLink.From(factory.Emails.SentTo("secret@example.com")[0]).Token);
        using (var forgot = await client.ForgotPasswordAsync("secret@example.com"))
        {
            forgot.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var link = ConfirmationLink.From(await factory.Emails.WaitForEmailAsync("secret@example.com", _emailTimeout, Ct, count: 2));
        secrets.Add(link.Token);
        secrets.Add(link.Uri.Query.TrimStart('?'));
        using (var reset = await client.ResetPasswordAsync(link.UserId, link.Token, NewPassword))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var tokens = await client.LoginTokensAsync("secret@example.com", NewPassword);
        secrets.AddRange([tokens.AccessToken, tokens.RefreshToken]);
        using var setup = await client.MfaSetupAsync(tokens.AccessToken);
        var key = (await setup.Content.ReadDataAsync<MfaSetupBody>(Ct))!.SharedKey;
        using var verify = await client.MfaVerifyAsync(tokens.AccessToken, Rfc6238.Code(key, clock.GetUtcNow()));
        var codes = (await verify.Content.ReadDataAsync<RecoveryCodesBody>(Ct))!.RecoveryCodes;
        secrets.Add(key);
        secrets.AddRange(codes);

        var mfaToken = await client.LoginMfaTokenAsync("secret@example.com", NewPassword);
        secrets.Add(mfaToken);
        using (var wrong = await client.LoginMfaAsync(mfaToken, recoveryCode: "AAAAA-BBBBB"))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var complete = await client.LoginMfaAsync(mfaToken, recoveryCode: codes[0]))
        {
            complete.StatusCode.ShouldBe(HttpStatusCode.OK);
            var signedIn = (await complete.Content.ReadDataAsync<TokenBody>(Ct))!;
            secrets.AddRange([signedIn.AccessToken, signedIn.RefreshToken]);
        }

        using (var change = await client.ChangePasswordAsync(tokens.AccessToken, NewPassword, "another new passphrase"))
        {
            change.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        secrets.Add("another new passphrase");

        var entries = recorder.Entries;
        entries.ShouldContain(e => e.Message.Contains("SecurityEvent PasswordReset", StringComparison.Ordinal));
        entries.ShouldContain(e => e.Message.Contains("SecurityEvent MfaSucceeded", StringComparison.Ordinal));
        foreach (var entry in entries)
        {
            var text = entry.Message + entry.Exception;
            foreach (var secret in secrets)
            {
                text.Contains(secret, StringComparison.Ordinal).ShouldBeFalse($"{entry.Category} logged a secret: {entry.Message}");
                text.Contains(Uri.EscapeDataString(secret), StringComparison.Ordinal).ShouldBeFalse($"{entry.Category} logged an encoded secret");
            }
        }
    }
}
