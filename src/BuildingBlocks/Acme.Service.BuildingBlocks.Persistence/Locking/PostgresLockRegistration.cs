using Acme.Service.BuildingBlocks.Locking;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Persistence.Locking;

/// <summary>
/// Provider registration for <c>Locks:Provider=Postgres</c> (D-02, D-06): registers
/// <see cref="IDistributedLock"/> on Postgres advisory locks, using <c>ConnectionStrings:Postgres</c>
/// (validated on start).
/// </summary>
public sealed class PostgresLockRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "Postgres";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Locks;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PostgresLockOptions>()
            .Configure(o => o.ConnectionString = configuration.GetConnectionString(ConnectionStringNames.App))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                $"ConnectionStrings:{ConnectionStringNames.App} is required when Locks:Provider is {ProviderName}.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IDistributedLock, PostgresDistributedLock>();
    }
}

/// <summary>Settings of <see cref="PostgresDistributedLock"/>.</summary>
internal sealed class PostgresLockOptions
{
    /// <summary>The app connection string (<c>ConnectionStrings:Postgres</c>).</summary>
    public string? ConnectionString { get; set; }
}
