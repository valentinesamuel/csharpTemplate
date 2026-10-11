using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Persistence.Health;
using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;
using Acme.Service.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Persistence;

[Collection(PostgresTestGroup.Name)]
public sealed class PersistenceHealthCheckTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PendingMigrationsCheck_PendingThenMigrated_UnhealthyUntilCacheExpiresThenHealthy()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        await using var app = WidgetsHost.BuildServices(connectionString);
        var clock = new ManualTimeProvider();
        using var check = new PendingMigrationsHealthCheck(
            app.GetServices<MigrationTarget>(), app.GetRequiredService<IServiceScopeFactory>(), clock);

        var before = await check.CheckHealthAsync(Context(check), Ct);

        await using (var migrator = WidgetsHost.BuildServices(connectionString, migrationRunner: true))
        {
            await migrator.GetRequiredService<MigrationRunner>().RunAsync(Ct);
        }

        var cached = await check.CheckHealthAsync(Context(check), Ct);
        clock.Advance(PendingMigrationsHealthCheck.CacheDuration + TimeSpan.FromSeconds(1));
        var after = await check.CheckHealthAsync(Context(check), Ct);

        before.Status.ShouldBe(HealthStatus.Unhealthy);
        before.Description.ShouldNotBeNull().ShouldContain(WidgetsDbContext.ModuleKey);
        cached.Status.ShouldBe(HealthStatus.Unhealthy);
        after.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostgresCheck_Reachable_ReturnsHealthy()
    {
        var check = new PostgresHealthCheck(WidgetsHost.Configuration(postgres.ConnectionString));

        var result = await check.CheckHealthAsync(Context(check), Ct);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostgresCheck_Unreachable_ReturnsUnhealthyWithoutConnectionDetails()
    {
        const string password = "do-not-leak";
        var check = new PostgresHealthCheck(WidgetsHost.Configuration(
            $"Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Password={password};Timeout=2"));

        var result = await check.CheckHealthAsync(Context(check), Ct);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldNotBeNull().ShouldNotContain(password);
        result.Exception.ShouldNotBeNull().ToString().ShouldNotContain(password);
    }

    internal static HealthCheckContext Context(IHealthCheck check) =>
        new() { Registration = new HealthCheckRegistration("test", check, HealthStatus.Unhealthy, tags: null) };

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
