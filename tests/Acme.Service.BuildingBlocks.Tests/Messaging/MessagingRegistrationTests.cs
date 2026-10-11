using Acme.Service.BuildingBlocks.Locking;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Persistence.Inbox;
using Acme.Service.BuildingBlocks.Persistence.Locking;
using Acme.Service.BuildingBlocks.Persistence.Messaging;
using Acme.Service.BuildingBlocks.Persistence.Outbox;
using Acme.Service.BuildingBlocks.Providers;
using Acme.Service.BuildingBlocks.Tests.Messaging.Fixtures;
using Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Messaging;

/// <summary>Options, backoff, lock key and registrations; no database needed.</summary>
public sealed class MessagingRegistrationTests
{
    private const string Connection = "Host=app-db;Database=app;Username=app";

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(5, 32)]
    [InlineData(8, 256)]
    [InlineData(9, 300)]
    [InlineData(10, 300)]
    [InlineData(1000, 300)]
    [InlineData(0, 2)]
    public void ComputeBackoff_Attempts_IsPowerOfTwoSecondsCappedAtFiveMinutes(int attempts, int expectedSeconds)
    {
        OutboxDispatcher<WidgetsDbContext>.ComputeBackoff(attempts).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public async Task Options_NoConfiguration_UsePlanDefaults()
    {
        await using var provider = Build([]);

        var outbox = provider.GetRequiredService<IOptions<OutboxOptions>>().Value;
        var inbox = provider.GetRequiredService<IOptions<InboxOptions>>().Value;

        outbox.PollingInterval.ShouldBe(TimeSpan.FromSeconds(2));
        outbox.BatchSize.ShouldBe(50);
        outbox.MaxAttempts.ShouldBe(10);
        outbox.RetentionDays.ShouldBe(7);
        inbox.RetentionDays.ShouldBe(30);
    }

    [Fact]
    public async Task Options_ConfiguredValues_AreBound()
    {
        await using var provider = Build(new()
        {
            ["Outbox:PollingInterval"] = "00:00:05",
            ["Outbox:BatchSize"] = "10",
            ["Outbox:MaxAttempts"] = "3",
            ["Outbox:RetentionDays"] = "2",
            ["Inbox:RetentionDays"] = "60",
        });

        var outbox = provider.GetRequiredService<IOptions<OutboxOptions>>().Value;

        outbox.PollingInterval.ShouldBe(TimeSpan.FromSeconds(5));
        outbox.BatchSize.ShouldBe(10);
        outbox.MaxAttempts.ShouldBe(3);
        outbox.RetentionDays.ShouldBe(2);
        provider.GetRequiredService<IOptions<InboxOptions>>().Value.RetentionDays.ShouldBe(60);
    }

    [Theory]
    [InlineData("Outbox:PollingInterval", "00:00:00", "Outbox:PollingInterval must be greater than zero.")]
    [InlineData("Outbox:PollingInterval", "soon", "Outbox:PollingInterval must be a time span (for example 00:00:02).")]
    [InlineData("Outbox:BatchSize", "0", "Outbox:BatchSize must be between 1 and 1000.")]
    [InlineData("Outbox:BatchSize", "1001", "Outbox:BatchSize must be between 1 and 1000.")]
    [InlineData("Outbox:MaxAttempts", "0", "Outbox:MaxAttempts must be at least 1.")]
    [InlineData("Outbox:RetentionDays", "many", "Outbox:RetentionDays must be an integer.")]
    [InlineData("Outbox:RetentionDays", "0", "Outbox:RetentionDays must be at least 1.")]
    public async Task OutboxOptions_InvalidValue_FailsNamingTheKey(string key, string value, string expected)
    {
        await using var provider = Build(new() { [key] = value });

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<OutboxOptions>>().Value)
            .Failures.ShouldContain(expected);
    }

    [Theory]
    [InlineData("0", "Inbox:RetentionDays must be at least 1.")]
    [InlineData("x", "Inbox:RetentionDays must be an integer.")]
    public async Task InboxOptions_InvalidValue_FailsNamingTheKey(string value, string expected)
    {
        await using var provider = Build(new() { ["Inbox:RetentionDays"] = value });

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<InboxOptions>>().Value)
            .Failures.ShouldContain(expected);
    }

    [Fact]
    public void ComputeKey_KnownVectors_MatchFnv1a64()
    {
        PostgresDistributedLock.ComputeKey(string.Empty).ShouldBe(unchecked((long)0xcbf29ce484222325));
        PostgresDistributedLock.ComputeKey("a").ShouldBe(unchecked((long)0xaf63dc4c8601ec8c));
        PostgresDistributedLock.ComputeKey("foobar").ShouldBe(unchecked((long)0x85944171f73967e8));
    }

    [Fact]
    public void ComputeKey_SameName_IsStableAndDistinctNamesDiffer()
    {
        PostgresDistributedLock.ComputeKey("jobs.cleanup").ShouldBe(PostgresDistributedLock.ComputeKey("jobs.cleanup"));
        PostgresDistributedLock.ComputeKey("jobs.cleanup").ShouldNotBe(PostgresDistributedLock.ComputeKey("jobs.Cleanup"));
    }

    [Fact]
    public async Task PostgresLockRegistration_Register_ProvidesPostgresDistributedLock()
    {
        var registration = new PostgresLockRegistration();
        var services = new ServiceCollection();
        services.AddLogging();

        registration.Register(services, Configuration(new() { ["ConnectionStrings:Postgres"] = Connection }));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        registration.Category.ShouldBe(ProviderCategory.Locks);
        registration.Name.ShouldBe("Postgres");
        provider.GetRequiredService<IDistributedLock>().ShouldBeOfType<PostgresDistributedLock>();
    }

    [Fact]
    public async Task PostgresLockRegistration_ConnectionStringMissing_FailsNamingTheKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new PostgresLockRegistration().Register(services, Configuration([]));
        await using var provider = services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<PostgresLockOptions>>().Value)
            .Failures.ShouldContain("ConnectionStrings:Postgres is required when Locks:Provider is Postgres.");
    }

    [Fact]
    public void AddIntegrationEventHandler_SameConsumerTwice_Throws()
    {
        var services = new ServiceCollection();
        services.AddIntegrationEventHandler<WidgetOrderedV1, AuditWidgetHandler>(WidgetsDbContext.ModuleKey);

        Should.Throw<InvalidOperationException>(
                () => services.AddIntegrationEventHandler<WidgetOrderedV1, AuditWidgetHandler>(WidgetsDbContext.ModuleKey))
            .Message.ShouldContain("'widgets.AuditWidgetHandler'");
    }

    [Fact]
    public void AddIntegrationEventHandler_ExplicitConsumerName_IsUsed()
    {
        var services = new ServiceCollection();

        services.AddIntegrationEventHandler<WidgetOrderedV1, AuditWidgetHandler>(WidgetsDbContext.ModuleKey, "widgets.audit");

        services.Select(d => d.ImplementationInstance).OfType<IntegrationEventSubscription>()
            .ShouldHaveSingleItem().ConsumerName.ShouldBe("widgets.audit");
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        values["ConnectionStrings:Postgres"] = Connection;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModuleDbContext<WidgetsDbContext>(Configuration(values), WidgetsDbContext.ModuleKey);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
