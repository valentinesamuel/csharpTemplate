using System.Security.Claims;
using System.Text.Encodings.Web;
using Acme.Service.BuildingBlocks.Web.Correlation;
using Acme.Service.BuildingBlocks.Web.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Tests.Web.Fixtures;

/// <summary>
/// Minimal in-memory web host (TestServer) with the P2 ProblemDetails and correlation setup and a header-driven
/// test authentication scheme: <c>X-Test-Sub</c> authenticates, <c>X-Test-Perm</c> (comma-separated) adds <c>perm</c> claims.
/// </summary>
internal static class WebTestHost
{
    public static async Task<WebApplication> StartAsync(
        Action<WebApplicationBuilder>? configureBuilder,
        Action<WebApplication> mapEndpoints)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddCorrelationId();
        builder.Services.AddProblemDetailsSetup();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();

        configureBuilder?.Invoke(builder);

        var app = builder.Build();
        app.UseCorrelationId();
        app.UseProblemDetailsSetup();
        app.UseAuthentication();
        app.UseAuthorization();
        mapEndpoints(app);

        await app.StartAsync();
        return app;
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var sub = Request.Headers["X-Test-Sub"].ToString();
            if (string.IsNullOrEmpty(sub))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new("sub", sub) };
            foreach (var perm in Request.Headers["X-Test-Perm"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                claims.Add(new Claim("perm", perm));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
