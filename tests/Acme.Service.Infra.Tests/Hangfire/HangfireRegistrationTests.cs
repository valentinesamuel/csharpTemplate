using Acme.Service.BuildingBlocks.Infra.Hangfire;
using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Acme.Service.BuildingBlocks.Providers;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.Infra.Tests.Hangfire;

public sealed class HangfireRegistrationTests
{
    private const string Password = "hangfire-registration-secret-c41e";
    private const string ConnectionString = $"Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Password={Password}";

    [Fact]
    public void Registration_DeclaresJobsCategoryAndHangfireName()
    {
        var registration = new HangfireRegistration();

        registration.Category.ShouldBe(ProviderCategory.Jobs);
        registration.Name.ShouldBe("Hangfire");
    }

    [Fact]
    public async Task Register_ValidConfiguration_ResolvesQueueAndStorageWithoutConnecting()
    {
        await using var provider = Build(Valid());

        var queue = provider.GetRequiredService<IJobQueue>();

        queue.ShouldBeOfType<HangfireJobQueue>();
        provider.GetRequiredService<IJobQueue>().ShouldBeSameAs(queue);
        provider.GetRequiredService<JobStorage>().ShouldBeSameAs(provider.GetRequiredService<JobStorage>());
        provider.GetRequiredService<JobActivator>().ShouldBeOfType<HangfireJobActivator>();
    }

    [Fact]
    public async Task Register_StorageIsOwnedByContainerNotStatic()
    {
        await using var first = Build(Valid());
        await using var second = Build(Valid());

        first.GetRequiredService<JobStorage>().ShouldNotBeSameAs(second.GetRequiredService<JobStorage>());
    }

    [Fact]
    public async Task Register_Defaults_HangfireSchemaDashboardOffAndHangfireWorkerCount()
    {
        await using var provider = Build(Valid());

        var options = provider.GetRequiredService<IOptions<HangfireOptions>>().Value;

        options.Schema.ShouldBe("hangfire");
        options.DashboardEnabled.ShouldBeFalse();
        options.WorkerCount.ShouldBe(Math.Min(Environment.ProcessorCount * 5, 20));
        options.QueuePollInterval.ShouldBe(TimeSpan.FromSeconds(15));
        options.SchedulePollingInterval.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Register_ConfiguredValues_AreBound()
    {
        await using var provider = Build(Valid(new()
        {
            ["Jobs:Hangfire:Schema"] = "jobs",
            ["Jobs:Hangfire:WorkerCount"] = "3",
            ["Jobs:Hangfire:DashboardEnabled"] = "true",
        }));

        var options = provider.GetRequiredService<IOptions<HangfireOptions>>().Value;

        options.Schema.ShouldBe("jobs");
        options.WorkerCount.ShouldBe(3);
        options.DashboardEnabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("jobs")]
    public void ToStorageOptions_Always_NeverPreparesSchema(string? schema)
    {
        var options = new HangfireOptions { Schema = schema ?? HangfireOptions.DefaultSchema, QueuePollInterval = TimeSpan.FromSeconds(3) };

        var storage = options.ToStorageOptions();

        storage.PrepareSchemaIfNecessary.ShouldBeFalse();
        storage.SchemaName.ShouldBe(options.Schema);
        storage.QueuePollInterval.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Register_MissingConnectionString_ValidationNamesKey()
    {
        await using var provider = Build(Valid(new() { ["ConnectionStrings:Postgres"] = null }));

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<HangfireOptions>>().Value);

        exception.Message.ShouldContain("ConnectionStrings:Postgres is required when Jobs:Provider is Hangfire.");
    }

    [Theory]
    [InlineData("Jobs:Hangfire:Schema", "Hangfire")]
    [InlineData("Jobs:Hangfire:Schema", "jobs; DROP SCHEMA public")]
    [InlineData("Jobs:Hangfire:Schema", "1jobs")]
    [InlineData("Jobs:Hangfire:WorkerCount", "0")]
    [InlineData("Jobs:Hangfire:WorkerCount", "1001")]
    [InlineData("Jobs:Hangfire:WorkerCount", "many")]
    [InlineData("Jobs:Hangfire:DashboardEnabled", "yes")]
    public async Task Register_InvalidSetting_ValidationNamesKeyWithoutEchoingConnectionString(string key, string value)
    {
        await using var provider = Build(Valid(new() { [key] = value }));

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<HangfireOptions>>().Value);

        exception.Message.ShouldContain(key);
        exception.Message.ShouldNotContain(Password);
    }

    [Theory]
    [InlineData("Hangfire")]
    [InlineData("hangfire")]
    [InlineData(" Hangfire ")]
    public void AddSchemaMigration_JobsProviderHangfire_RegistersSchemaStep(string provider)
    {
        var services = new ServiceCollection();

        var added = HangfireRegistration.AddSchemaMigration(services, Configuration(new() { ["Jobs:Provider"] = provider }));

        added.ShouldBeTrue();
        services.ShouldHaveSingleItem().ImplementationType.ShouldBe(typeof(HangfireSchemaPostMigrationStep));
        services[0].ServiceType.ShouldBe(typeof(IPostMigrationStep));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("InMemory")]
    public void AddSchemaMigration_OtherJobsProvider_RegistersNothing(string? provider)
    {
        var services = new ServiceCollection();

        var added = HangfireRegistration.AddSchemaMigration(services, Configuration(new() { ["Jobs:Provider"] = provider }));

        added.ShouldBeFalse();
        services.ShouldBeEmpty();
    }

    [Fact]
    public void SchemaStep_OrderAndName_RunAfterModuleMigrationsBeforeSeeders()
    {
        var step = new HangfireSchemaPostMigrationStep(Configuration([]), NullLogger<HangfireSchemaPostMigrationStep>.Instance);

        step.Name.ShouldBe("hangfire-schema");
        step.Order.ShouldBe(100);
    }

    [Fact]
    public async Task SchemaStep_MissingMigrationConnectionString_ThrowsNamingKey()
    {
        var step = new HangfireSchemaPostMigrationStep(Configuration([]), NullLogger<HangfireSchemaPostMigrationStep>.Instance);

        var exception = await Should.ThrowAsync<OptionsValidationException>(() => step.ExecuteAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("ConnectionStrings:PostgresMigrations is required");
    }

    [Fact]
    public async Task SchemaStep_InvalidSchema_ThrowsNamingKeyWithoutConnecting()
    {
        var step = new HangfireSchemaPostMigrationStep(
            Configuration(new() { ["ConnectionStrings:PostgresMigrations"] = ConnectionString, ["Jobs:Hangfire:Schema"] = "bad-schema" }),
            NullLogger<HangfireSchemaPostMigrationStep>.Instance);

        var exception = await Should.ThrowAsync<OptionsValidationException>(() => step.ExecuteAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("Jobs:Hangfire:Schema");
        exception.Message.ShouldNotContain(Password);
    }

    [Fact]
    public void RoundUpToSecond_FractionalSecond_RoundsUpAndKeepsWholeSeconds()
    {
        var whole = new DateTimeOffset(2026, 1, 1, 0, 0, 5, TimeSpan.Zero);

        HangfireJobQueue.RoundUpToSecond(whole).ShouldBe(whole);
        HangfireJobQueue.RoundUpToSecond(whole.AddTicks(1)).ShouldBe(whole.AddSeconds(1));
        HangfireJobQueue.RoundUpToSecond(whole.AddMilliseconds(999)).ShouldBe(whole.AddSeconds(1));
    }

    [Fact]
    public async Task ScheduleAsync_NegativeDelay_ThrowsArgumentOutOfRange()
    {
        await using var provider = Build(Valid());
        var queue = provider.GetRequiredService<IJobQueue>();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => queue.ScheduleAsync<NoopJob, int>(1, TimeSpan.FromSeconds(-1), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AddOrUpdateRecurringAsync_BlankCron_ThrowsArgumentExceptionWithoutConnecting(string cron)
    {
        await using var provider = Build(Valid());
        var queue = provider.GetRequiredService<IJobQueue>();

        await Should.ThrowAsync<ArgumentException>(
            () => queue.AddOrUpdateRecurringAsync<NoopRecurringJob>("id", cron, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MapDashboard_EnabledInDevelopment_MapsDashboard()
    {
        await using var app = BuildWebApp(Environments.Development, dashboardEnabled: true);

        var endpoint = app.MapHangfireDashboardIfEnabled();

        endpoint.ShouldNotBeNull();
        RoutePatterns(app).ShouldContain(p => p.StartsWith("/hangfire", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("Development", false)]
    public async Task MapDashboard_NotDevelopmentOrNotEnabled_MapsNothing(string environment, bool dashboardEnabled)
    {
        await using var app = BuildWebApp(environment, dashboardEnabled);

        var endpoint = app.MapHangfireDashboardIfEnabled();

        endpoint.ShouldBeNull();
        RoutePatterns(app).ShouldNotContain(p => p.StartsWith("/hangfire", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MapDashboard_HangfireNotSelected_MapsNothing()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:Hangfire:DashboardEnabled"] = "true" });
        await using var app = builder.Build();

        app.MapHangfireDashboardIfEnabled().ShouldBeNull();
    }

    private static WebApplication BuildWebApp(string environment, bool dashboardEnabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(Valid(new()
        {
            ["Jobs:Hangfire:DashboardEnabled"] = dashboardEnabled ? "true" : "false",
        }));
        new HangfireRegistration().Register(builder.Services, builder.Configuration);
        return builder.Build();
    }

    private static List<string> RoutePatterns(IEndpointRouteBuilder endpoints) =>
        [.. endpoints.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? string.Empty)];

    private static Dictionary<string, string?> Valid(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = ConnectionString };
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = Configuration(settings);
        var services = new ServiceCollection();
        services.AddLogging();
        new HangfireRegistration().Register(services, configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class NoopJob : IJob<int>
    {
        public Task ExecuteAsync(int payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoopRecurringJob : IRecurringJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
