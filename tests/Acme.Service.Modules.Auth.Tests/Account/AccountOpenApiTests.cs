using System.Net;
using System.Text.Json;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Account;

/// <summary>Every account endpoint is in the OpenAPI document; only the anonymous ones lack the bearer requirement (D-24).</summary>
public sealed class AccountOpenApiTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenApiDocument_AccountEndpoints_AreDescribedWithTheirSecurity()
    {
        await using var factory = new ProbeWebFactory(overrides: new Dictionary<string, string?> { ["OpenApi:Enabled"] = "true" });

        using var response = await ProbeClient.GetAsync(factory, "/openapi/v1.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var (path, method, anonymous) in new[]
        {
            ("/api/v1/auth/register", "post", true),
            ("/api/v1/auth/confirm-email", "post", true),
            ("/api/v1/auth/resend-confirmation", "post", true),
            ("/api/v1/auth/login", "post", true),
            ("/api/v1/auth/refresh", "post", true),
            ("/api/v1/auth/logout", "post", false),
            ("/api/v1/auth/logout-all", "post", false),
            ("/api/v1/auth/me", "get", false),
            ("/api/v1/auth/login/mfa", "post", true),
            ("/api/v1/auth/forgot-password", "post", true),
            ("/api/v1/auth/reset-password", "post", true),
            ("/api/v1/auth/change-password", "post", false),
            ("/api/v1/auth/mfa/authenticator", "post", false),
            ("/api/v1/auth/mfa/authenticator/verify", "post", false),
            ("/api/v1/auth/mfa/disable", "post", false),
            ("/api/v1/auth/mfa/recovery-codes", "post", false),
        })
        {
            paths.TryGetProperty(path, out var item).ShouldBeTrue(path);
            item.TryGetProperty(method, out var operation).ShouldBeTrue($"{method} {path}");
            operation.TryGetProperty("security", out _).ShouldBe(!anonymous, $"{method} {path}");
        }
    }
}
