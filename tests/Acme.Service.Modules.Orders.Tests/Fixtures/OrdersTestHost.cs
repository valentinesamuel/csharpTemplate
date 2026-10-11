using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Acme.Service.Host.Commands;
using Acme.Service.Host.Modules;
using Acme.Service.Modules.Auth;
using Acme.Service.Testing.Containers;
using Acme.Service.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace Acme.Service.Modules.Orders.Tests.Fixtures;

/// <summary>
/// One migrated test database (the real <c>migrate</c> verb: auth, then orders, then the permission seeder) and the web
/// host over it. Test users get real access tokens: registered through the Auth API, confirmed and given exactly one
/// seed role in the database, then logged in, so their <c>perm</c> claims come from the seeded catalog (§0.8).
/// </summary>
internal sealed class OrdersTestHost : IAsyncDisposable
{
    public const string Password = "correct horse battery";

    private OrdersTestHost(OrdersWebFactory factory)
    {
        Factory = factory;
    }

    public OrdersWebFactory Factory { get; }

    public string ConnectionString => Factory.ConnectionString;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<OrdersTestHost> CreateAsync(
        PostgresFixture postgres,
        IReadOnlyDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        var exitCode = await MigrateCommand.RunAsync(
            ["migrate", $"--ConnectionStrings:PostgresMigrations={connectionString}", "--Jobs:Provider=InMemory"],
            HostModules.Create(),
            TextWriter.Null,
            Ct);
        exitCode.ShouldBe(0);
        return new OrdersTestHost(new OrdersWebFactory(connectionString, overrides, configureServices));
    }

    /// <summary>Creates a confirmed user whose only role is <paramref name="role"/> and logs it in.</summary>
    public async Task<TestUser> UserAsync(string email, string role)
    {
        using var client = Factory.CreateClient();
        using (var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password }, Ct))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var userId = await ScalarAsync<Guid>(
            "UPDATE auth.users SET email_confirmed = true WHERE normalized_email = @email RETURNING id",
            ("email", email.ToUpperInvariant()));

        if (role != "Customer")
        {
            (await AuthModule.GrantRoleAsync(Factory.Services, email, role, Ct)).ShouldBe(0);
            await ScalarAsync<int>(
                "WITH removed AS (DELETE FROM auth.user_roles ur USING auth.roles r WHERE ur.role_id = r.id AND r.name = 'Customer' AND ur.user_id = @id RETURNING 1) SELECT count(*)::int FROM removed",
                ("id", userId));
        }

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(Ct));
        var body = await login.Content.ReadDataElementAsync(Ct);
        return new TestUser(userId, body.GetProperty("accessToken").GetString()!);
    }

    /// <summary>Sends a request with an optional bearer token and JSON body.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, TestUser? user, object? body = null) =>
        SendAsync(method, path, user, body is null ? null : JsonContent.Create(body));

    /// <summary>Sends a request with an optional bearer token, content and extra headers; redirects are not followed.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        TestUser? user,
        HttpContent? content,
        params (string Name, string Value)[] headers)
    {
        using var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        if (user is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return await client.SendAsync(request, Ct);
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

            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        }
    }

    /// <summary>Places an order as <paramref name="user"/> and returns it, asserting 201.</summary>
    public async Task<JsonElement> CreateOrderAsync(TestUser user, params (string Sku, int Quantity, decimal UnitPrice)[] lines)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/orders", user, CreateBody("EUR", lines));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await JsonAsync(response);
    }

    public static object CreateBody(string currency, params (string Sku, int Quantity, decimal UnitPrice)[] lines) =>
        new { currency, lines = lines.Select(l => new { sku = l.Sku, quantity = l.Quantity, unitPrice = l.UnitPrice }).ToArray() };

    public static object LinesBody(uint version, params (string Sku, int Quantity, decimal UnitPrice)[] lines) =>
        new { version, lines = lines.Select(l => new { sku = l.Sku, quantity = l.Quantity, unitPrice = l.UnitPrice }).ToArray() };

    /// <summary>The <c>data</c> of the success envelope as a detached JSON element.</summary>
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        return await response.Content.ReadDataElementAsync(Ct);
    }

    /// <summary>The <c>code</c> extension of a problem response, asserting the problem content type.</summary>
    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var body = document.RootElement;
        body.GetProperty("isSuccess").GetBoolean().ShouldBeFalse();
        body.TryGetProperty("requestId", out _).ShouldBeTrue();
        body.TryGetProperty("traceId", out _).ShouldBeTrue();
        body.TryGetProperty("correlationId", out _).ShouldBeTrue();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>Runs a scalar SQL statement against the test database.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)result;
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>A logged-in test user.</summary>
/// <param name="Id">User id (<c>sub</c>).</param>
/// <param name="AccessToken">A valid access token.</param>
internal sealed record TestUser(Guid Id, string AccessToken);
