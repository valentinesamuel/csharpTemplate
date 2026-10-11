using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Providers;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.Hangfire;

/// <summary>
/// <c>Jobs:Provider=Hangfire</c>: a persistent <see cref="IJobQueue"/> on Hangfire with Postgres storage
/// (<c>ConnectionStrings:Postgres</c>, schema <c>Jobs:Hangfire:Schema</c>, default <c>hangfire</c>) plus a Hangfire server
/// in every replica (<c>Jobs:Hangfire:WorkerCount</c> workers). Options are validated at startup; startup never contacts
/// the database and never creates the schema (INV-16): run <c>migrate</c>, which calls <see cref="AddSchemaMigration"/>.
/// </summary>
/// <remarks>
/// Storage, client, recurring-job manager and job activator are owned by the DI container (registered before
/// <c>AddHangfire</c>, whose registrations are try-adds), so Hangfire's static <c>JobStorage.Current</c> is never used.
/// Hangfire's process-wide settings (data compatibility level, serializer settings, log provider) are still global, as
/// they are in every Hangfire application; the log provider (<see cref="HangfireLogProvider"/>) tolerates a disposed
/// container.
/// </remarks>
public sealed class HangfireRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "Hangfire";

    /// <summary>Configuration key that selects the jobs provider.</summary>
    public const string ProviderKey = "Jobs:Provider";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Jobs;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<HangfireOptions>()
            .Configure(o => HangfireOptions.Read(configuration, o))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<HangfireOptions>, HangfireOptions.Validator>());

        // Registered before AddHangfire so its try-adds keep these instead of the static JobStorage/JobActivator.
        services.AddSingleton<JobStorage>(sp => CreateStorage(sp.GetRequiredService<IOptions<HangfireOptions>>().Value));
        services.AddSingleton<JobActivator>(sp => new HangfireJobActivator(sp));

        services.AddHangfire((sp, config) => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseLogProvider(new HangfireLogProvider(sp.GetRequiredService<ILoggerFactory>())));
        services.AddHangfireServer((sp, server) =>
        {
            var options = sp.GetRequiredService<IOptions<HangfireOptions>>().Value;
            server.WorkerCount = options.WorkerCount;
            server.SchedulePollingInterval = options.SchedulePollingInterval;
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<HangfireJobQueue>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<HangfireJobQueue>());
    }

    /// <summary>
    /// For the <c>migrate</c> verb, which does not run the provider registry: when <c>Jobs:Provider</c> is
    /// <c>Hangfire</c>, registers <see cref="HangfireSchemaPostMigrationStep"/> so that <c>migrate</c> installs the
    /// Hangfire schema after the module migrations. Otherwise registers nothing. Never called by the web app (INV-16).
    /// </summary>
    /// <returns>True when the step was registered.</returns>
    public static bool AddSchemaMigration(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!string.Equals(configuration[ProviderKey]?.Trim(), ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        services.AddPostMigrationStep<HangfireSchemaPostMigrationStep>();
        return true;
    }

    private static PostgreSqlStorage CreateStorage(HangfireOptions options)
    {
        var storageOptions = options.ToStorageOptions();
        return new PostgreSqlStorage(new NpgsqlConnectionFactory(options.ConnectionString, storageOptions), storageOptions);
    }
}

/// <summary>
/// Creates Hangfire job instances from the root container. Only the runners (<see cref="HangfireJobRunner{TJob,TPayload}"/>,
/// <see cref="HangfireRecurringJobRunner{TJob}"/>) are ever enqueued, and they open their own scope per run.
/// </summary>
internal sealed class HangfireJobActivator(IServiceProvider services) : JobActivator
{
    /// <inheritdoc />
    public override object ActivateJob(Type jobType) => ActivatorUtilities.GetServiceOrCreateInstance(services, jobType);
}
