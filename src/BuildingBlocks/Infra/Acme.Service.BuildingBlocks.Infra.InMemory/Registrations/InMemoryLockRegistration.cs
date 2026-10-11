using Acme.Service.BuildingBlocks.Locking;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;

/// <summary>
/// <c>Locks:Provider=InMemory</c>: a process-local <see cref="IDistributedLock"/> that does not exclude other
/// replicas. Development and tests only (D-06, D-21).
/// </summary>
public sealed class InMemoryLockRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "InMemory";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Locks;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IDistributedLock, InMemoryDistributedLock>();
    }
}
