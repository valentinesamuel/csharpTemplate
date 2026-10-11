using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing.Logging;
using Acme.Service.Testing;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Password;

/// <summary>
/// INV-28 / LOG-01 on the JSON output: the same password and MFA flows as <see cref="SensitiveDataLoggingTests"/>, but
/// captured through the real Serilog pipeline and the production <c>JsonLogFormatter</c> at <c>Verbose</c>, so scope and
/// call-site properties (<c>meta</c>), <c>requestMeta</c> and <c>exception</c> are checked as well as the message.
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class SensitiveDataJsonLoggingTests(PostgresFixture postgres)
{
    private const string NewPassword = "a brand new passphrase";
    private const string Verbose = "Verbose";

    private static readonly TimeSpan _emailTimeout = TimeSpan.FromSeconds(30);

    // Every event formatted: the default level and every category override from appsettings.json raised to Verbose.
    private static readonly Dictionary<string, string?> _verbose = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Serilog:MinimumLevel:Default"] = Verbose,
        ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = Verbose,
        ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Diagnostics"] = Verbose,
        ["Serilog:MinimumLevel:Override:Microsoft.Hosting.Lifetime"] = Verbose,
        ["Serilog:MinimumLevel:Override:System.Net.Http.HttpClient"] = Verbose,
        ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore.Database.Command"] = Verbose,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PasswordAndMfaFlows_JsonLinesContainNoSecrets()
    {
        var capture = new JsonLogCapture();
        var (factory, clock) = await MfaTestHost.CreateAsync(postgres, _verbose, services => services.AddJsonLogCapture(capture));
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

        var lines = capture.Lines;
        var parsed = lines.Select(JsonLogCapture.ParseLine).ToList();

        // Non-vacuous: the security events (Information) reached the JSON capture, and so did Verbose/Debug events.
        parsed.ShouldContain(l => MessageOf(l).Contains("SecurityEvent PasswordReset", StringComparison.Ordinal));
        parsed.ShouldContain(l => MessageOf(l).Contains("SecurityEvent MfaSucceeded", StringComparison.Ordinal));
        parsed.ShouldContain(l => l.GetProperty("level").GetString() == "Verbose" || l.GetProperty("level").GetString() == "Debug");
        parsed.ShouldContain(l => l.GetProperty("meta").ValueKind == JsonValueKind.Object && l.GetProperty("meta").EnumerateObject().Any());

        var forms = secrets
            .Where(secret => !string.IsNullOrEmpty(secret))
            .SelectMany(Forms)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < lines.Count; i++)
        {
            // The full line as written (escaped JSON text) and the decoded string values inside it.
            var texts = new List<string> { lines[i] };
            CollectStrings(parsed[i], texts);
            foreach (var text in texts)
            {
                foreach (var form in forms)
                {
                    text.Contains(form, StringComparison.Ordinal).ShouldBeFalse($"A JSON log line contains a secret: {lines[i]}");
                }
            }
        }

        capture.ShouldHaveNoQueryInRequestPaths();
    }

    private static string MessageOf(JsonElement line) => line.GetProperty("message").GetString() ?? string.Empty;

    // The secret, its URI-escaped form and its JSON-escaped forms (relaxed encoder used by the formatter, and default).
    private static IEnumerable<string> Forms(string secret) =>
    [
        secret,
        Uri.EscapeDataString(secret),
        JsonEncodedText.Encode(secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
        JsonEncodedText.Encode(secret).ToString(),
    ];

    private static void CollectStrings(JsonElement element, List<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                into.Add(element.GetString()!);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    into.Add(property.Name);
                    CollectStrings(property.Value, into);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStrings(item, into);
                }

                break;
            default:
                break;
        }
    }
}
