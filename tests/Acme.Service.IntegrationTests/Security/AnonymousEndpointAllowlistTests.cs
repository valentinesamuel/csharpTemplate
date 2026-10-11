using Acme.Service.Testing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Acme.Service.IntegrationTests.Security;

/// <summary>
/// INV-12: every endpoint either carries authorization metadata, or is explicitly anonymous AND listed in
/// <see cref="AnonymousEndpointAllowlist"/>. An endpoint with neither fails, as does an unlisted anonymous one.
/// </summary>
public sealed class AnonymousEndpointAllowlistTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Endpoints_AllRegistered_AreAuthorizedOrAllowlistedAnonymous()
    {
        // OpenApi:Enabled maps the Development-only anonymous endpoints, so they are checked too.
        await using var factory = new ConfiguredWebFactory(new Dictionary<string, string?> { ["OpenApi:Enabled"] = "true" });

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        endpoints.ShouldNotBeEmpty();
        var patterns = endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        patterns.ShouldContain("/.well-known/jwks.json");
        patterns.ShouldContain("/api/v1/auth/login");
        patterns.ShouldContain("/api/v1/auth/logout-all");
        patterns.ShouldContain("/api/v1/auth/roles");
        patterns.ShouldNotContain("/api/v1/auth/external/oidc/challenge");
        var violations = AnonymousEndpointAudit.FindViolations(endpoints, AnonymousEndpointAllowlist.Patterns.Keys);
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Endpoints_WithExternalOidcEnabled_AreAuthorizedOrAllowlistedAnonymous()
    {
        // The external login endpoints exist only when enabled; check them too. Nothing contacts the provider here.
        await using var factory = new ConfiguredWebFactory(new Dictionary<string, string?>
        {
            ["Auth:External:Oidc:Enabled"] = "true",
            ["Auth:External:Oidc:Authority"] = "https://idp.example.test",
            ["Auth:External:Oidc:ClientId"] = "service",
            ["Auth:External:Oidc:ClientSecret"] = "not-a-real-secret",
            ["Auth:External:Oidc:AllowedReturnUrls:0"] = "https://app.example.test/auth/callback",
        });

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var patterns = endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        patterns.ShouldContain("/api/v1/auth/external/oidc/challenge");
        patterns.ShouldContain("/api/v1/auth/external/oidc/callback");
        patterns.ShouldContain("/api/v1/auth/external/exchange");
        var violations = AnonymousEndpointAudit.FindViolations(endpoints, AnonymousEndpointAllowlist.Patterns.Keys);
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FallbackPolicy_Host_RequiresAuthenticatedUser()
    {
        await using var factory = new ServiceWebFactory();

        var fallback = await factory.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();

        fallback.ShouldNotBeNull();
        fallback.Requirements.ShouldContain(r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void FindViolations_UnlistedAnonymousEndpoint_ReportsIt()
    {
        var endpoint = Endpoint("/secret-export", new AllowAnonymousAttribute());

        var violations = AnonymousEndpointAudit.FindViolations([endpoint], AnonymousEndpointAllowlist.Patterns.Keys);

        violations.ShouldHaveSingleItem().ShouldContain("/secret-export");
    }

    [Fact]
    public void FindViolations_EndpointWithoutAuthorizationMetadata_ReportsIt()
    {
        var endpoint = Endpoint("/api/v1/things");

        var violations = AnonymousEndpointAudit.FindViolations([endpoint], AnonymousEndpointAllowlist.Patterns.Keys);

        violations.ShouldHaveSingleItem().ShouldContain("/api/v1/things");
    }

    [Fact]
    public void FindViolations_AuthorizedAndAllowlistedEndpoints_ReportsNothing()
    {
        Endpoint[] endpoints =
        [
            Endpoint("/api/v1/things", new AuthorizeAttribute()),
            Endpoint("/api/v1/other", new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()),
            Endpoint("/health/live", new AllowAnonymousAttribute()),
        ];

        AnonymousEndpointAudit.FindViolations(endpoints, AnonymousEndpointAllowlist.Patterns.Keys).ShouldBeEmpty();
    }

    [Fact]
    public void FindViolations_AnonymousOverridesAuthorize_StillRequiresAllowlist()
    {
        // AllowAnonymous wins over [Authorize] at runtime, so authorization metadata does not excuse it.
        var endpoint = Endpoint("/api/v1/mixed", new AuthorizeAttribute(), new AllowAnonymousAttribute());

        AnonymousEndpointAudit.FindViolations([endpoint], AnonymousEndpointAllowlist.Patterns.Keys).ShouldHaveSingleItem();
    }

    private static RouteEndpoint Endpoint(string pattern, params object[] metadata) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, new EndpointMetadataCollection(metadata), pattern);

    private sealed class ConfiguredWebFactory(IReadOnlyDictionary<string, string?> overrides) : ServiceWebFactory
    {
        protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides => overrides;
    }
}

/// <summary>The rule of <see cref="AnonymousEndpointAllowlistTests"/>, separated so it can be tested on synthetic endpoints.</summary>
internal static class AnonymousEndpointAudit
{
    public static List<string> FindViolations(IEnumerable<Endpoint> endpoints, IEnumerable<string> allowlist)
    {
        var allowed = allowlist.ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var name = endpoint is RouteEndpoint route ? route.RoutePattern.RawText ?? endpoint.DisplayName : endpoint.DisplayName;
            var metadata = endpoint.Metadata;

            if (metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                if (name is null || !allowed.Contains(name))
                {
                    violations.Add($"'{name}' ({endpoint.DisplayName}) allows anonymous access but is not in AnonymousEndpointAllowlist.");
                }

                continue;
            }

            var hasAuthorization = metadata.GetMetadata<IAuthorizeData>() is not null
                || metadata.GetMetadata<AuthorizationPolicy>() is not null
                || metadata.GetMetadata<IAuthorizationRequirementData>() is not null;
            if (!hasAuthorization)
            {
                violations.Add($"'{name}' ({endpoint.DisplayName}) has no authorization metadata; call RequireAuthorization/RequirePermission or AllowAnonymous (and allowlist it).");
            }
        }

        return violations;
    }
}
