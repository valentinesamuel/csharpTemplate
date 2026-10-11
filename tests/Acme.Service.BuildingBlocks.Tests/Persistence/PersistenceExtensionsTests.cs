using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Persistence.Health;
using Acme.Service.BuildingBlocks.Persistence.Inbox;
using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Acme.Service.BuildingBlocks.Persistence.Outbox;
using Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Persistence;

/// <summary>Registration, model and mapping behaviour that needs no database.</summary>
public sealed class PersistenceExtensionsTests
{
    private const string AppConnection = "Host=app-db;Database=app;Username=app";
    private const string MigrationsConnection = "Host=ddl-db;Database=app;Username=owner";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddModuleDbContext_ResolverForModuleKey_ReturnsKeyedEfUnitOfWork()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IModuleUnitOfWorkResolver>().Resolve(WidgetsDbContext.ModuleKey);

        unitOfWork.ShouldBeOfType<EfUnitOfWork<WidgetsDbContext>>();
        unitOfWork.ShouldBeSameAs(scope.ServiceProvider.GetRequiredKeyedService<IUnitOfWork>(WidgetsDbContext.ModuleKey));
    }

    [Fact]
    public async Task Resolve_UnknownModule_ThrowsNamingTheModule()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IModuleUnitOfWorkResolver>();

        Should.Throw<InvalidOperationException>(() => resolver.Resolve("orders")).Message.ShouldContain("'orders'");
    }

    [Fact]
    public void AddModuleDbContext_SameModuleTwice_Throws()
    {
        var services = new ServiceCollection();
        services.AddModuleDbContext<WidgetsDbContext>(Configuration(), WidgetsDbContext.ModuleKey);

        Should.Throw<InvalidOperationException>(
            () => services.AddModuleDbContext<WidgetsDbContext>(Configuration(), WidgetsDbContext.ModuleKey));
    }

    [Fact]
    public async Task AddModuleDbContext_WebApp_UsesAppConnectionString()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();

        context.Database.GetConnectionString().ShouldBe(AppConnection);
    }

    [Fact]
    public async Task AddMigrationRunner_ContextsUseMigrationsConnectionString()
    {
        await using var provider = Build(services => services.AddMigrationRunner());
        await using var scope = provider.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();

        context.Database.GetConnectionString().ShouldBe(MigrationsConnection);
        provider.GetRequiredService<MigrationRunner>().Targets
            .ShouldHaveSingleItem().ShouldBe(new MigrationTarget(WidgetsDbContext.ModuleKey, typeof(WidgetsDbContext)));
    }

    [Fact]
    public async Task ResolveContext_ConnectionStringMissing_ThrowsNamingTheKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModuleDbContext<WidgetsDbContext>(new ConfigurationBuilder().Build(), WidgetsDbContext.ModuleKey);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<WidgetsDbContext>())
            .Message.ShouldBe("ConnectionStrings:Postgres is not configured.");
    }

    [Fact]
    public async Task Model_ModuleContext_UsesModuleSchemaSnakeCaseXminAndHistoryTableInSchema()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();

        var entity = context.Model.FindEntityType(typeof(Widget)).ShouldNotBeNull();
        var relational = RelationalOptionsExtension.Extract(context.GetService<IDbContextOptions>());

        context.Model.GetDefaultSchema().ShouldBe("widgets");
        entity.GetSchema().ShouldBe("widgets");
        entity.GetTableName().ShouldBe("widgets");
        entity.FindProperty(nameof(Widget.Quantity)).ShouldNotBeNull().GetColumnName().ShouldBe("quantity");
        entity.FindProperty(nameof(Widget.Version)).ShouldNotBeNull().GetColumnName().ShouldBe("xmin");
        relational.MigrationsHistoryTableName.ShouldBe("__ef_migrations_history");
        relational.MigrationsHistoryTableSchema.ShouldBe("widgets");
        relational.ExecutionStrategyFactory.ShouldBeNull();
    }

    [Fact]
    public async Task KeyedOutbox_ModuleKey_ResolvesEfOutboxOfTheModuleContext()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutbox>(WidgetsDbContext.ModuleKey);

        outbox.ShouldBeOfType<EfOutbox<WidgetsDbContext>>();
    }

    [Fact]
    public async Task AddModuleDbContext_RegistersDispatcherHostedServiceForTheModule()
    {
        await using var provider = Build();

        provider.GetServices<IHostedService>().OfType<OutboxDispatcher<WidgetsDbContext>>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AddModuleDbContext_RegistersOutboxCleanupSchedulerOnceAndRetention()
    {
        await using var provider = Build();

        var hosted = provider.GetServices<IHostedService>().ToList();

        hosted.Count.ShouldBe(2);
        hosted.OfType<OutboxCleanupScheduler>().ShouldHaveSingleItem();
        provider.GetRequiredService<IMessageRetention>().ShouldBeSameAs(provider.GetRequiredService<OutboxCleanup>());
    }

    [Fact]
    public async Task Model_ModuleContext_MapsOutboxAndInboxTablesInModuleSchema()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();

        var outbox = context.Model.FindEntityType(typeof(OutboxMessage)).ShouldNotBeNull();
        var inbox = context.Model.FindEntityType(typeof(InboxMessage)).ShouldNotBeNull();

        outbox.GetSchema().ShouldBe("widgets");
        outbox.GetTableName().ShouldBe("outbox_messages");
        outbox.FindProperty(nameof(OutboxMessage.Payload)).ShouldNotBeNull().GetColumnType().ShouldBe("jsonb");
        outbox.GetIndexes().ShouldHaveSingleItem().Properties.Select(p => p.GetColumnName())
            .ShouldBe(["processed_at", "dead_at", "next_attempt_at"]);
        inbox.GetSchema().ShouldBe("widgets");
        inbox.GetTableName().ShouldBe("inbox_messages");
        inbox.FindPrimaryKey().ShouldNotBeNull().Properties.Select(p => p.GetColumnName()).ShouldBe(["message_id", "consumer"]);
    }

    [Fact]
    public async Task PostgresCheck_ConnectionStringMissing_ReturnsUnhealthyNamingTheKey()
    {
        var check = new PostgresHealthCheck(new ConfigurationBuilder().Build());

        var result = await check.CheckHealthAsync(PersistenceHealthCheckTests.Context(check), Ct);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldBe("ConnectionStrings:Postgres is not configured.");
    }

    [Fact]
    public async Task PendingMigrationsCheck_NoModuleContexts_ReturnsHealthy()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var check = new PendingMigrationsHealthCheck([], services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        var result = await check.CheckHealthAsync(PersistenceHealthCheckTests.Context(check), Ct);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task AddPersistenceHealthChecks_Tag_RegistersPostgresAndMigrationsChecks()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddHealthChecks().AddPersistenceHealthChecks("ready");
        await using var provider = services.BuildServiceProvider();

        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        registrations.Select(r => r.Name).ShouldBe(["postgres", "migrations"], ignoreOrder: true);
        registrations.ShouldAllBe(r => r.Tags.Contains("ready"));
    }

    [Fact]
    public async Task RunAsync_NoModuleContexts_RunsOnlyPostMigrationSteps()
    {
        var step = Substitute.For<IPostMigrationStep>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMigrationRunner();
        services.AddScoped(_ => step);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<MigrationRunner>().RunAsync(Ct);

        await step.Received(1).ExecuteAsync(Arg.Any<CancellationToken>());
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = AppConnection,
                ["ConnectionStrings:PostgresMigrations"] = MigrationsConnection,
            })
            .Build();

    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModuleDbContext<WidgetsDbContext>(Configuration(), WidgetsDbContext.ModuleKey).AddRepository<Widget, Guid>();
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
