using System.Net;
using System.Security.Claims;
using Acme.Service.BuildingBlocks.Tests.Web.Fixtures;
using Acme.Service.BuildingBlocks.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Web;

public sealed class PermissionAuthorizationTests
{
    [Fact]
    public async Task HandleAsync_MatchingPermClaim_Succeeds()
    {
        var context = await HandleAsync("orders.read", Authenticated(new Claim("perm", "orders.read")));

        context.HasSucceeded.ShouldBeTrue();
    }

    [Fact]
    public async Task HandleAsync_NoPermClaim_DoesNotSucceed()
    {
        var context = await HandleAsync("orders.read", Authenticated(new Claim("sub", "user-1")));

        context.HasSucceeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData("orders.create")]
    [InlineData("Orders.Read")]
    [InlineData("orders.read ")]
    public async Task HandleAsync_DifferentPermValue_DoesNotSucceed(string granted)
    {
        var context = await HandleAsync("orders.read", Authenticated(new Claim("perm", granted)));

        context.HasSucceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task HandleAsync_ClaimOnUnauthenticatedIdentity_DoesNotSucceed()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("perm", "orders.read")]));

        var context = await HandleAsync("orders.read", user);

        context.HasSucceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task GetPolicyAsync_PermPrefix_BuildsAuthenticatedPermissionPolicy()
    {
        var provider = CreateProvider();

        var policy = await provider.GetPolicyAsync("perm:orders.read");

        policy.ShouldNotBeNull();
        policy.Requirements.OfType<PermissionRequirement>().Single().Permission.ShouldBe("orders.read");
        policy.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GetPolicyAsync_NamedNonPermPolicy_FallsBackToConfiguredPolicy()
    {
        var configured = new AuthorizationPolicyBuilder().RequireRole("Admin").Build();
        var provider = CreateProvider(o => o.AddPolicy("live-session", configured));

        (await provider.GetPolicyAsync("live-session")).ShouldBeSameAs(configured);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("perm:")]
    [InlineData("PERM:orders.read")]
    public async Task GetPolicyAsync_UnknownNonPermPolicy_ReturnsNull(string name)
    {
        var provider = CreateProvider();

        (await provider.GetPolicyAsync(name)).ShouldBeNull();
    }

    [Fact]
    public async Task GetFallbackAndDefaultPolicy_DelegateToOptions()
    {
        var fallback = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        var provider = CreateProvider(o => o.FallbackPolicy = fallback);

        (await provider.GetFallbackPolicyAsync()).ShouldBeSameAs(fallback);
        (await provider.GetDefaultPolicyAsync()).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.Unauthorized)]
    [InlineData("user-1", "", HttpStatusCode.Forbidden)]
    [InlineData("user-1", "orders.create", HttpStatusCode.Forbidden)]
    [InlineData("user-1", "orders.create,orders.read", HttpStatusCode.OK)]
    public async Task RequirePermission_Endpoint_EnforcesPermClaim(string? sub, string? perms, HttpStatusCode expected)
    {
        await using var app = await WebTestHost.StartAsync(
            builder => builder.Services.AddPermissionAuthorization(),
            a => a.MapGet("/orders", () => "ok").RequirePermission("orders.read"));
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders");
        if (sub is not null)
        {
            request.Headers.Add("X-Test-Sub", sub);
            request.Headers.Add("X-Test-Perm", perms);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("orders")]
    [InlineData("Orders.Read")]
    [InlineData("orders..read")]
    [InlineData("orders.read ")]
    public void RequirePermission_InvalidName_Throws(string permission)
    {
        var builder = Substitute.For<IEndpointConventionBuilder>();

        Should.Throw<ArgumentException>(() => builder.RequirePermission(permission));
    }

    [Fact]
    public void AddPermissionAuthorization_ReplacesPolicyProvider()
    {
        using var services = new ServiceCollection().AddLogging().AddPermissionAuthorization().BuildServiceProvider();

        services.GetRequiredService<IAuthorizationPolicyProvider>().ShouldBeOfType<PermissionPolicyProvider>();
        services.GetServices<IAuthorizationHandler>().OfType<PermissionAuthorizationHandler>().ShouldHaveSingleItem();
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test"));

    private static PermissionPolicyProvider CreateProvider(Action<AuthorizationOptions>? configure = null)
    {
        var options = new AuthorizationOptions();
        configure?.Invoke(options);
        return new PermissionPolicyProvider(Options.Create(options));
    }

    private static async Task<AuthorizationHandlerContext> HandleAsync(string permission, ClaimsPrincipal user)
    {
        var requirement = new PermissionRequirement(permission);
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await new PermissionAuthorizationHandler().HandleAsync(context);
        return context;
    }
}
