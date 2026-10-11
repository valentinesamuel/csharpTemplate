using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Acme.Service.Testing.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.BuildingBlocks.Tests.Persistence.Fixtures;

/// <summary>Shares one Postgres container across the persistence integration tests.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

/// <summary>
/// A service provider with the <c>widgets</c> module context registered the way a module does it, on its
/// own database.
/// </summary>
internal sealed class WidgetsHost : IAsyncDisposable
{
    private WidgetsHost(ServiceProvider services, string connectionString)
    {
        Services = services;
        ConnectionString = connectionString;
    }

    public ServiceProvider Services { get; }

    public string ConnectionString { get; }

    public static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["ConnectionStrings:PostgresMigrations"] = connectionString,
            })
            .Build();

    public static ServiceProvider BuildServices(string connectionString, bool migrationRunner = false, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModuleDbContext<WidgetsDbContext>(Configuration(connectionString), WidgetsDbContext.ModuleKey)
            .AddRepository<Widget, Guid>();
        if (migrationRunner)
        {
            services.AddMigrationRunner();
        }

        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Creates a fresh database, migrates it and returns the app-side provider.</summary>
    public static async Task<WidgetsHost> CreateAsync(PostgresFixture postgres, CancellationToken cancellationToken, Action<IServiceCollection>? configure = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

        await using (var migrator = BuildServices(connectionString, migrationRunner: true))
        {
            await migrator.GetRequiredService<MigrationRunner>().RunAsync(cancellationToken);
        }

        return new WidgetsHost(BuildServices(connectionString, configure: configure), connectionString);
    }

    public async Task<Widget> SeedAsync(string name, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();
        var widget = new Widget { Name = name, Quantity = 1 };
        context.Widgets.Add(widget);
        await context.SaveChangesAsync(cancellationToken);
        return widget;
    }

    public async Task<List<Widget>> ReadAllAsync(CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WidgetsDbContext>();
        return await context.Widgets.AsNoTracking().OrderBy(w => w.Name).ToListAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
