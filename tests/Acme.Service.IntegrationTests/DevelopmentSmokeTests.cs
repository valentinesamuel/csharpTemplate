using System.Net;
using Acme.Service.IntegrationTests.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Acme.Service.IntegrationTests;

/// <summary>
/// The other suites run in the <c>Testing</c> environment, which skips the container's build-time validation. In
/// <c>Development</c> the host validates every registration and its scopes when it builds (ValidateOnBuild and
/// ValidateScopes), so a broken registration fails here; the Development-only endpoints are checked too.
/// </summary>
public sealed class DevelopmentSmokeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Host_DevelopmentEnvironment_BuildsWithValidatedServicesAndServes()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await ServiceApi.MigrateAsync(connectionString);
        await using var factory = new DevelopmentWebFactory(connectionString);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        using var openApi = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);
        using var me = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative), Ct);
        using var anonymous = await client.GetAsync(new Uri(ClaimsProbeModule.ClaimsPath, UriKind.Relative), Ct);

        factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.ShouldBe(Environments.Development);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK, await ready.Content.ReadAsStringAsync(Ct));
        openApi.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await openApi.Content.ReadAsStringAsync(Ct)).ShouldContain("Bearer");
        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private sealed class DevelopmentWebFactory(string connectionString) : IntegrationWebFactory(connectionString)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment(Environments.Development);
        }
    }
}
