using Acme.Service.BuildingBlocks.Caching;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;

/// <summary>
/// <c>Cache:Provider=InMemory</c>: a process-local <see cref="ICache"/>. Allowed in every environment; with
/// several replicas each has its own cache (D-21: the Host warns when idempotency relies on it).
/// </summary>
public sealed class InMemoryCacheRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "InMemory";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Cache;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<ICache, InMemoryCache>();
    }
}
