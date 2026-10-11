using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Acme.Service.Host.Commands;
using Acme.Service.Host.Modules;
using Acme.Service.Modules.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Shouldly;

namespace Acme.Service.IntegrationTests.Fixtures;

/// <summary>
/// The HTTP API and the test database as the P19 suites use them. Test users get real tokens the way the Orders tests
/// mint them: registered through the Auth API, confirmed in SQL, given a seed role with the public
/// <see cref="AuthModule.GrantRoleAsync"/> entry point, then logged in, so their <c>perm</c> claims come from the seeded
/// catalog (§0.8).
/// </summary>
internal sealed class ServiceApi(IntegrationWebFactory factory)
{
    public const string Password = "correct horse battery";

    public IntegrationWebFactory Factory => factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Runs the real <c>migrate</c> verb (auth, then the other modules, then the seeder; the Hangfire schema when
    /// <paramref name="jobsProvider"/> is <c>Hangfire</c>) and asserts exit code 0.
    /// </summary>
    public static async Task MigrateAsync(string connectionString, string jobsProvider = "InMemory")
    {
        var exitCode = await MigrateCommand.RunAsync(
            ["migrate", $"--ConnectionStrings:PostgresMigrations={connectionString}", $"--Jobs:Provider={jobsProvider}"],
            HostModules.Create(),
            TextWriter.Null,
            Ct);
        exitCode.ShouldBe(0);
    }

    /// <summary>
    /// <paramref name="connectionString"/> with short connect and command timeouts, so requests against a frozen server
    /// fail (transiently) in seconds rather than after Npgsql's defaults of 15 and 30 seconds.
    /// </summary>
    public static string WithShortTimeouts(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 3, CommandTimeout = 3, CancellationTimeout = 500 }.ConnectionString;

    /// <summary>Registers <paramref name="email"/> through the API, asserting 202.</summary>
    public async Task RegisterAsync(string email)
    {
        using var response = await PostAsync("/api/v1/auth/register", new { email, password = Password });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    /// <summary>Creates a confirmed user whose only role is <paramref name="role"/> and logs it in.</summary>
    public async Task<TestUser> UserAsync(string email, string role = "Customer")
    {
        await RegisterAsync(email);
        var userId = await ScalarAsync<Guid>(
            "UPDATE auth.users SET email_confirmed = true WHERE normalized_email = @email RETURNING id",
            ("email", email.ToUpperInvariant()));

        if (role != "Customer")
        {
            (await AuthModule.GrantRoleAsync(factory.Services, email, role, Ct)).ShouldBe(0);
            await ScalarAsync<int>(
                "WITH removed AS (DELETE FROM auth.user_roles ur USING auth.roles r WHERE ur.role_id = r.id AND r.name = 'Customer' AND ur.user_id = @id RETURNING 1) SELECT count(*)::int FROM removed",
                ("id", userId));
        }

        var tokens = await LoginTokensAsync(email);
        return new TestUser(userId, email, tokens.AccessToken, tokens.RefreshToken);
    }

    /// <summary>Logs in, asserting 200 and a token response.</summary>
    public async Task<TokenPair> LoginTokensAsync(string email, string password = Password)
    {
        using var response = await LoginAsync(email, password);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await TokensOfAsync(response);
    }

    /// <summary>
    /// Enables two-factor authentication for the caller of <paramref name="accessToken"/> through the API (setup, then
    /// verification with the current time step's code) and returns the shared key.
    /// </summary>
    public async Task<string> EnableMfaAsync(string accessToken)
    {
        using var setup = await PostAsync("/api/v1/auth/mfa/authenticator", null, accessToken);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK, await setup.Content.ReadAsStringAsync(Ct));
        var sharedKey = (await JsonAsync(setup)).GetProperty("sharedKey").GetString()!;

        using var verify = await PostAsync("/api/v1/auth/mfa/authenticator/verify", new { code = Totp.Code(sharedKey, DateTimeOffset.UtcNow) }, accessToken);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK, await verify.Content.ReadAsStringAsync(Ct));
        (await JsonAsync(verify)).GetProperty("recoveryCodes").GetArrayLength().ShouldBeGreaterThan(0);
        return sharedKey;
    }

    /// <summary>Logs in an account with two-factor authentication, asserting 200 with an mfa token and no tokens.</summary>
    public async Task<string> LoginMfaTokenAsync(string email)
    {
        using var response = await LoginAsync(email);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await JsonAsync(response);
        body.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        body.TryGetProperty("accessToken", out _).ShouldBeFalse();
        return body.GetProperty("mfaToken").GetString()!;
    }

    /// <summary>
    /// Completes the mfa step with the NEXT time step's code: the step used to enable MFA is spent (replay guard) and
    /// the server tolerates one step of drift, so this works whenever the step boundary falls.
    /// </summary>
    public Task<HttpResponseMessage> CompleteMfaAsync(string mfaToken, string sharedKey) =>
        PostAsync("/api/v1/auth/login/mfa", new { mfaToken, code = Totp.Code(sharedKey, DateTimeOffset.UtcNow + Totp.Step) });

    public Task<HttpResponseMessage> LoginAsync(string email, string password = Password) =>
        PostAsync("/api/v1/auth/login", new { email, password });

    public Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        PostAsync("/api/v1/auth/refresh", new { refreshToken });

    /// <summary>Sends a JSON POST with an optional bearer token.</summary>
    public Task<HttpResponseMessage> PostAsync(string path, object? body, string? accessToken = null, params (string Name, string Value)[] headers) =>
        SendAsync(HttpMethod.Post, path, accessToken, body is null ? null : JsonContent.Create(body), headers);

    /// <summary>Sends a GET with an optional bearer token.</summary>
    public Task<HttpResponseMessage> GetAsync(string path, string? accessToken = null) =>
        SendAsync(HttpMethod.Get, path, accessToken, null);

    /// <summary>Sends a request with an optional bearer token, content and headers; redirects are not followed.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? accessToken,
        HttpContent? content,
        params (string Name, string Value)[] headers)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Runs a scalar SQL statement against the test database.</summary>
    public Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters) =>
        ScalarAsync<T>(factory.ConnectionString, sql, parameters);

    /// <summary>Runs a scalar SQL statement against <paramref name="connectionString"/>.</summary>
    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)result;
    }

    /// <summary>The token pair of a login or refresh response.</summary>
    public static async Task<TokenPair> TokensOfAsync(HttpResponseMessage response)
    {
        var body = await JsonAsync(response);
        return new TokenPair(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    /// <summary>The response body as a detached JSON element.</summary>
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    /// <summary>The <c>code</c> extension of a problem response, asserting the RFC 7807 shape (INV-18).</summary>
    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var body = await JsonAsync(response);
        body.TryGetProperty("traceId", out _).ShouldBeTrue();
        body.TryGetProperty("correlationId", out _).ShouldBeTrue();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>Polls <paramref name="condition"/> until it holds, failing after <paramref name="timeout"/> (10 s by default).</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {because}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    /// <summary>
    /// Sends <paramref name="send"/> until it returns <paramref name="expected"/> (failing after
    /// <paramref name="timeout"/>, 30 s by default) and returns that response; used after an outage ends.
    /// </summary>
    public static async Task<HttpResponseMessage> UntilStatusAsync(
        Func<Task<HttpResponseMessage>> send,
        HttpStatusCode expected,
        string because,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var response = await send();
            if (response.StatusCode == expected)
            {
                return response;
            }

            var status = response.StatusCode;
            response.Dispose();
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {because}; last status {(int)status}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }
}

/// <summary>An access and refresh token pair.</summary>
internal sealed record TokenPair(string AccessToken, string RefreshToken);

/// <summary>A logged-in test user.</summary>
internal sealed record TestUser(Guid Id, string Email, string AccessToken, string RefreshToken);
