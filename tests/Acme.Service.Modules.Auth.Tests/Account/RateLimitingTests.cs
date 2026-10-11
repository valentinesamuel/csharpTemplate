using System.Net;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Account;

/// <summary>
/// The account rate limits (D-12) with their configured defaults: <c>auth-credentials</c> 10 per minute per client IP,
/// <c>auth-email</c> 5 per minute per client IP and per normalized email. Rejections are 429 ProblemDetails with
/// <c>Retry-After</c>.
/// </summary>
[Collection(AuthDatabaseGroup.Name)]
public sealed class RateLimitingTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Login_EleventhAttemptWithinAMinute_Returns429WithRetryAfter()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory, "203.0.113.10");
        using var otherClient = new AccountClient(factory, "203.0.113.11");

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var allowed = await client.LoginAsync("nobody@example.com");
            allowed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var limited = await client.LoginAsync("nobody@example.com");
        using var refreshLimited = await client.RefreshAsync("anything");
        using var otherIp = await otherClient.LoginAsync("nobody@example.com");

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await AccountClient.ProblemCodeAsync(limited)).ShouldBe("too_many_requests");
        var retryAfter = limited.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull();
        retryAfter.ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        // The same policy (and partition) covers refresh; another client IP has its own window.
        refreshLimited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        otherIp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Register_SixthRequestForOneEmailFromDifferentIps_Returns429()
    {
        await using var factory = await CreateHostAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var client = new AccountClient(factory, $"198.51.100.{attempt + 1}");
            var email = attempt % 2 == 0 ? "target@example.com" : "TARGET@example.com";
            using var allowed = attempt < 3 ? await client.RegisterAsync(email) : await client.ResendConfirmationAsync(email);
            allowed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        using var sixthClient = new AccountClient(factory, "198.51.100.6");
        using var limited = await sixthClient.ResendConfirmationAsync(" Target@Example.com ");
        using var otherEmail = await sixthClient.ResendConfirmationAsync("other@example.com");

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await AccountClient.ProblemCodeAsync(limited)).ShouldBe("too_many_requests");
        limited.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        otherEmail.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResendConfirmation_SixthRequestFromOneIpForDifferentEmails_Returns429()
    {
        await using var factory = await CreateHostAsync();
        using var client = new AccountClient(factory, "192.0.2.20");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var allowed = await client.ResendConfirmationAsync($"user{attempt}@example.com");
            allowed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        using var limited = await client.ResendConfirmationAsync("user5@example.com");

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await AccountClient.ProblemCodeAsync(limited)).ShouldBe("too_many_requests");
        limited.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    private async Task<AccountWebFactory> CreateHostAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await AuthTestServices.MigrateAsync(connectionString);

        // The shipped defaults (appsettings.json), instead of the high limits AccountWebFactory uses elsewhere.
        return new AccountWebFactory(connectionString, new Dictionary<string, string?>
        {
            ["Auth:RateLimiting:Credentials:PermitLimit"] = "10",
            ["Auth:RateLimiting:Credentials:Window"] = "00:01:00",
            ["Auth:RateLimiting:Email:PermitLimit"] = "5",
            ["Auth:RateLimiting:Email:Window"] = "00:01:00",
        });
    }
}
