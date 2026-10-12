using Acme.Service.BuildingBlocks.Correlation;
using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Persistence.Health;
using Acme.Service.BuildingBlocks.Persistence.Inbox;
using Acme.Service.BuildingBlocks.Persistence.Messaging;
using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Acme.Service.BuildingBlocks.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Persistence;

/// <summary>Registration of module persistence, the migration runner and the readiness checks.</summary>
public static class PersistenceExtensions
{
    /// <summary>Name of the Postgres connectivity check.</summary>
    public const string PostgresCheckName = "postgres";

    /// <summary>Name of the pending-migrations check.</summary>
    public const string MigrationsCheckName = "migrations";

    /// <summary>
    /// Registers a module context: <typeparamref name="TContext"/> on Npgsql with its migrations assembly and
    /// a history table in the module schema; the keyed scoped <see cref="IUnitOfWork"/>, <see cref="IOutbox"/>
    /// (<see cref="EfOutbox{TContext}"/>) and inbox store (keyed by <paramref name="moduleKey"/>, D-04); the
    /// module's <see cref="OutboxDispatcher{TContext}"/> hosted service; the shared messaging services and
    /// validated <c>Outbox</c>/<c>Inbox</c> options; the shared <see cref="OutboxCleanupScheduler"/>, which registers
    /// the recurring <see cref="OutboxCleanupJob"/> with the selected <c>IJobQueue</c> at startup; the
    /// <see cref="IModuleUnitOfWorkResolver"/>; and a <see cref="MigrationTarget"/>. Call from <c>IModule.AddPersistence</c>.
    /// </summary>
    /// <remarks>
    /// The context uses <c>ConnectionStrings:Postgres</c>, or <c>ConnectionStrings:PostgresMigrations</c> after
    /// <see cref="AddMigrationRunner"/>. A missing connection string fails when the context is first resolved.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration holding the connection strings.</param>
    /// <param name="moduleKey">The module name; equal to the context schema.</param>
    public static ModulePersistenceBuilder<TContext> AddModuleDbContext<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string moduleKey)
        where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);

        if (services.Any(d => d.ServiceType == typeof(MigrationTarget)
                && d.ImplementationInstance is MigrationTarget existing
                && (existing.ModuleKey == moduleKey || existing.ContextType == typeof(TContext))))
        {
            throw new InvalidOperationException(
                $"A module context is already registered for module '{moduleKey}' or context '{typeof(TContext).Name}'.");
        }

        services.AddDbContext<TContext>(
            (serviceProvider, options) =>
            {
                var name = serviceProvider.GetService<ConnectionStringSelection>()?.Name ?? ConnectionStringNames.App;
                var connectionString = configuration.GetConnectionString(name);
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException($"ConnectionStrings:{name} is not configured.");
                }

                NpgsqlContextOptions.Configure(options, connectionString, moduleKey, typeof(TContext).Assembly);
            },
            optionsLifetime: ServiceLifetime.Singleton);

        services.AddKeyedScoped<IUnitOfWork, EfUnitOfWork<TContext>>(moduleKey);
        services.TryAddScoped<IModuleUnitOfWorkResolver, ModuleUnitOfWorkResolver>();
        services.AddSingleton(new MigrationTarget(moduleKey, typeof(TContext)));

        // Messaging (P6): the module's outbox, inbox and dispatcher; shared bus, registry and options.
        services.AddMessagingCore();
        OutboxOptions.Register(services, configuration);
        InboxOptions.Register(services, configuration);
        services.TryAddSingleton<OutboxCleanup>();
        services.TryAddSingleton<IMessageRetention>(sp => sp.GetRequiredService<OutboxCleanup>());

        // Retention (P10): one recurring OutboxCleanupJob for all modules, registered with the selected IJobQueue at startup.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutboxCleanupScheduler>());
        services.AddKeyedScoped<IOutbox>(moduleKey, (sp, _) => new EfOutbox<TContext>(
            sp.GetRequiredService<TContext>(),
            sp.GetRequiredService<IntegrationEventSerializer>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ICorrelationContext>()));
        services.AddKeyedScoped<IInboxStore>(moduleKey, (sp, _) => new EfInboxStore<TContext>(sp.GetRequiredService<TContext>()));
        services.AddHostedService(sp => new OutboxDispatcher<TContext>(
            moduleKey,
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<IntegrationEventTypeRegistry>(),
            sp.GetRequiredService<IOptions<OutboxOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<OutboxMetrics>(),
            sp.GetRequiredService<ILogger<OutboxDispatcher<TContext>>>(),
            sp.GetService<ICorrelationContext>()));

        return new ModulePersistenceBuilder<TContext>(services, moduleKey);
    }

    /// <summary>
    /// Registers the <see cref="MigrationRunner"/> and switches module contexts to
    /// <c>ConnectionStrings:PostgresMigrations</c> (DDL rights). For the <c>migrate</c> command only; the
    /// web host composition never calls this (INV-16); only the migrate command does.
    /// </summary>
    public static IServiceCollection AddMigrationRunner(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<ConnectionStringSelection>();
        services.AddSingleton(new ConnectionStringSelection(ConnectionStringNames.Migrations));
        services.TryAddSingleton<MigrationRunner>();
        return services;
    }

    /// <summary>Registers a scoped <see cref="IPostMigrationStep"/> run by the <c>migrate</c> command.</summary>
    public static IServiceCollection AddPostMigrationStep<TStep>(this IServiceCollection services)
        where TStep : class, IPostMigrationStep
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPostMigrationStep, TStep>());
        return services;
    }

    /// <summary>
    /// Adds the readiness checks (D-20): <see cref="PostgresHealthCheck"/> and
    /// <see cref="PendingMigrationsHealthCheck"/> (cached 30 s), both with <paramref name="tags"/>.
    /// </summary>
    public static IHealthChecksBuilder AddPersistenceHealthChecks(this IHealthChecksBuilder builder, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<PendingMigrationsHealthCheck>();

        builder.AddCheck<PostgresHealthCheck>(PostgresCheckName, HealthStatus.Unhealthy, tags, TimeSpan.FromSeconds(5));
        builder.AddCheck<PendingMigrationsHealthCheck>(MigrationsCheckName, HealthStatus.Unhealthy, tags, TimeSpan.FromSeconds(10));
        return builder;
    }
}

/// <summary>Adds repositories for a module context registered with <see cref="PersistenceExtensions.AddModuleDbContext{TContext}"/>.</summary>
/// <typeparam name="TContext">The module context.</typeparam>
public sealed class ModulePersistenceBuilder<TContext>
    where TContext : ModuleDbContext
{
    internal ModulePersistenceBuilder(IServiceCollection services, string moduleKey)
    {
        Services = services;
        ModuleKey = moduleKey;
    }

    /// <summary>The service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The module name.</summary>
    public string ModuleKey { get; }

    /// <summary>Registers <see cref="EfRepository{TContext,TEntity,TId}"/> as the scoped <see cref="IRepository{TEntity,TId}"/>.</summary>
    public ModulePersistenceBuilder<TContext> AddRepository<TEntity, TId>()
        where TEntity : class
        where TId : notnull
    {
        Services.TryAddScoped<IRepository<TEntity, TId>, EfRepository<TContext, TEntity, TId>>();
        return this;
    }
}
