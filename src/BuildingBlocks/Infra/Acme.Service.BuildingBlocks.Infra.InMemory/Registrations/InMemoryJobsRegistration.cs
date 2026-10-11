using Acme.Service.BuildingBlocks.Jobs;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;

/// <summary>
/// <c>Jobs:Provider=InMemory</c>: an in-process <see cref="IJobQueue"/> plus its background worker. Jobs are
/// lost on restart. Development only (D-21).
/// </summary>
public sealed class InMemoryJobsRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "InMemory";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Jobs;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        InMemoryJobQueueOptions.Register(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<InMemoryJobQueue>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<InMemoryJobQueue>());
        services.AddHostedService<InMemoryJobWorker>();
    }
}
