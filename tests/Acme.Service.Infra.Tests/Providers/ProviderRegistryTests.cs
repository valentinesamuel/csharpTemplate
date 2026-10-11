using Acme.Service.BuildingBlocks.Caching;
using Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;
using Acme.Service.BuildingBlocks.Locking;
using Acme.Service.BuildingBlocks.Persistence.Locking;
using Acme.Service.BuildingBlocks.Providers;
using Acme.Service.BuildingBlocks.Web.Idempotency;
using Acme.Service.Host.Providers;
using Acme.Service.Infra.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Acme.Service.Infra.Tests.Providers;

public sealed class ProviderRegistryTests
{
    private static readonly Dictionary<string, string?> _validDevelopment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cache:Provider"] = "InMemory",
        ["Blob:Provider"] = "Local",
        ["Blob:Local:RootPath"] = Path.Combine(Path.GetTempPath(), "service-tests", "registry"),
        ["Notifications:Provider"] = "Console",
        ["Jobs:Provider"] = "InMemory",
        ["Locks:Provider"] = "InMemory",
    };

    [Fact]
    public void Apply_ValidSelection_RegistersOneProviderPerCategory()
    {
        var services = new ServiceCollection();

        var selections = Registry().Apply(services, Config(_validDevelopment), Environment(Environments.Development));

        selections.Select(s => s.Category).ShouldBe(Enum.GetValues<ProviderCategory>());
        services.Count(d => d.ServiceType == typeof(ICache)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(IDistributedLock)).ShouldBe(1);
        selections.Single(s => s.Category == ProviderCategory.Locks).Registration.ShouldBeOfType<InMemoryLockRegistration>();
    }

    [Fact]
    public void Apply_ValidSelection_ExposesSelectionAsNamedOptions()
    {
        var services = new ServiceCollection();
        Registry().Apply(services, Config(_validDevelopment), Environment(Environments.Development));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<ProviderOptions>>();

        options.Get("Cache").Provider.ShouldBe("InMemory");
        options.Get("Blob").Provider.ShouldBe("Local");
    }

    [Fact]
    public void Apply_ProviderNameInDifferentCase_Matches()
    {
        var settings = With(_validDevelopment, "Locks:Provider", "postgres");
        settings["ConnectionStrings:Postgres"] = "Host=localhost";

        var selections = Registry().Apply(new ServiceCollection(), Config(settings), Environment(Environments.Development));

        selections.Single(s => s.Category == ProviderCategory.Locks).Registration.ShouldBeOfType<PostgresLockRegistration>();
    }

    [Theory]
    [InlineData("Cache:Provider")]
    [InlineData("Blob:Provider")]
    [InlineData("Notifications:Provider")]
    [InlineData("Jobs:Provider")]
    [InlineData("Locks:Provider")]
    public void Apply_MissingProvider_FailsNamingKey(string key)
    {
        var exception = ApplyFailing(With(_validDevelopment, key, null), Environments.Development);

        var failure = exception.Failures.ShouldHaveSingleItem();
        failure.ShouldStartWith($"{key} is required.");
    }

    [Fact]
    public void Apply_BlankProvider_FailsNamingKey()
    {
        var exception = ApplyFailing(With(_validDevelopment, "Cache:Provider", "  "), Environments.Development);

        exception.Failures.ShouldHaveSingleItem().ShouldStartWith("Cache:Provider is required.");
    }

    [Fact]
    public void Apply_UnknownProvider_FailsNamingKeyAndListingRegisteredNames()
    {
        var exception = ApplyFailing(With(_validDevelopment, "Cache:Provider", "Foo"), Environments.Development);

        var failure = exception.Failures.ShouldHaveSingleItem();
        failure.ShouldContain("Cache:Provider value 'Foo' does not match a registered provider");
        failure.ShouldContain("Registered providers: InMemory.");
        exception.Message.ShouldContain("Cache:Provider");
    }

    [Fact]
    public void Apply_ValueMatchesTwoRegistrations_Fails()
    {
        var registry = Registry().Add<InMemoryCacheRegistration>();

        var exception = Should.Throw<OptionsValidationException>(
            () => registry.Apply(new ServiceCollection(), Config(_validDevelopment), Environment(Environments.Development)));

        exception.Failures.ShouldHaveSingleItem().ShouldContain("Cache:Provider value 'InMemory' matches 2 registrations");
    }

    [Fact]
    public void Apply_CategoryWithoutRegistrations_FailsListingNone()
    {
        var registry = new ProviderRegistry()
            .Add<LocalBlobRegistration>()
            .Add<ConsoleNotificationsRegistration>()
            .Add<InMemoryJobsRegistration>()
            .Add<InMemoryLockRegistration>();

        var exception = Should.Throw<OptionsValidationException>(
            () => registry.Apply(new ServiceCollection(), Config(_validDevelopment), Environment(Environments.Development)));

        exception.Failures.ShouldHaveSingleItem().ShouldBe("Cache:Provider value 'InMemory' does not match a registered provider. Registered providers: none.");
    }

    [Theory]
    [InlineData("Blob:Provider", "Local")]
    [InlineData("Notifications:Provider", "Console")]
    [InlineData("Jobs:Provider", "InMemory")]
    [InlineData("Locks:Provider", "InMemory")]
    public void Apply_DevelopmentOnlyProviderInProduction_FailsNamingKey(string key, string provider)
    {
        var settings = ProductionSettings();
        settings[key] = provider;

        var exception = ApplyFailing(settings, Environments.Production, ProductionSafeRegistry());

        exception.Failures.ShouldHaveSingleItem()
            .ShouldBe($"{key}={provider} is a development-only provider and is not allowed in the Production environment (D-21).");
    }

    [Fact]
    public void Apply_ProductionWithProductionSafeProviders_Succeeds()
    {
        var selections = ProductionSafeRegistry().Apply(new ServiceCollection(), Config(ProductionSettings()), Environment(Environments.Production));

        selections.Single(s => s.Category == ProviderCategory.Cache).Registration.Name.ShouldBe("InMemory");
        selections.Single(s => s.Category == ProviderCategory.Locks).Registration.Name.ShouldBe("Postgres");
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Apply_DevelopmentOnlyProvidersOutsideProduction_Succeeds(string environment) =>
        Registry().Apply(new ServiceCollection(), Config(_validDevelopment), Environment(environment)).Count.ShouldBe(5);

    [Fact]
    public void Apply_SeveralProblems_ReportsAllAndRegistersNothing()
    {
        var settings = With(With(_validDevelopment, "Cache:Provider", "Foo"), "Jobs:Provider", null);
        var services = new ServiceCollection();

        var exception = Should.Throw<OptionsValidationException>(
            () => Registry().Apply(services, Config(settings), Environment(Environments.Development)));

        exception.Failures.Count().ShouldBe(2);
        services.ShouldBeEmpty();
    }

    [Fact]
    public void AddProviders_CalledTwice_Throws()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(_validDevelopment);
        builder.AddProviders(p => AddAll(p));

        Should.Throw<InvalidOperationException>(() => builder.AddProviders(p => AddAll(p)));
    }

    [Fact]
    public async Task StartupReporter_InMemoryCacheWithIdempotency_LogsWarning()
    {
        var logs = await RunReporterAsync(withIdempotency: true);

        logs.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("Cache:Provider=InMemory with idempotency enabled", StringComparison.Ordinal));
        logs.Count(e => e.Level == LogLevel.Information && e.Message.StartsWith("Provider ", StringComparison.Ordinal)).ShouldBe(5);
    }

    [Fact]
    public async Task StartupReporter_InMemoryCacheWithoutIdempotency_DoesNotWarn()
    {
        var logs = await RunReporterAsync(withIdempotency: false);

        logs.ShouldNotContain(e => e.Level == LogLevel.Warning);
    }

    private static async Task<IReadOnlyList<LogEntry>> RunReporterAsync(bool withIdempotency)
    {
        using var logs = new RecordingLoggerProvider();
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(_validDevelopment);
        builder.Logging.AddProvider(logs);
        builder.AddProviders(p => AddAll(p));
        if (withIdempotency)
        {
            builder.Services.AddIdempotency(builder.Configuration);
        }

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        return logs.Entries;
    }

    private static OptionsValidationException ApplyFailing(Dictionary<string, string?> settings, string environment, ProviderRegistry? registry = null) =>
        Should.Throw<OptionsValidationException>(
            () => (registry ?? Registry()).Apply(new ServiceCollection(), Config(settings), Environment(environment)));

    private static ProviderRegistry Registry() => AddAll(new ProviderRegistry());

    private static ProviderRegistry AddAll(ProviderRegistry registry) =>
        registry
            .Add<InMemoryCacheRegistration>()
            .Add<LocalBlobRegistration>()
            .Add<ConsoleNotificationsRegistration>()
            .Add<InMemoryJobsRegistration>()
            .Add<InMemoryLockRegistration>()
            .Add<PostgresLockRegistration>();

    /// <summary>
    /// Production-capable stand-ins for the categories whose real providers arrive in P9/P10, so each D-21 test
    /// isolates one category.
    /// </summary>
    private static ProviderRegistry ProductionSafeRegistry() =>
        Registry()
            .Add(new StubRegistration(ProviderCategory.Blob, "S3"))
            .Add(new StubRegistration(ProviderCategory.Notifications, "Smtp"))
            .Add(new StubRegistration(ProviderCategory.Jobs, "Hangfire"));

    private static Dictionary<string, string?> ProductionSettings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cache:Provider"] = "InMemory",
        ["Blob:Provider"] = "S3",
        ["Blob:Local:RootPath"] = Path.Combine(Path.GetTempPath(), "service-tests", "registry"),
        ["Notifications:Provider"] = "Smtp",
        ["Jobs:Provider"] = "Hangfire",
        ["Locks:Provider"] = "Postgres",
        ["ConnectionStrings:Postgres"] = "Host=localhost",
    };

    private static Dictionary<string, string?> With(Dictionary<string, string?> source, string key, string? value) =>
        new(source, StringComparer.OrdinalIgnoreCase) { [key] = value };

    private static IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static IHostEnvironment Environment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }

    private sealed class StubRegistration(ProviderCategory category, string name) : IProviderRegistration
    {
        public ProviderCategory Category => category;

        public string Name => name;

        public void Register(IServiceCollection services, IConfiguration configuration)
        {
        }
    }
}
