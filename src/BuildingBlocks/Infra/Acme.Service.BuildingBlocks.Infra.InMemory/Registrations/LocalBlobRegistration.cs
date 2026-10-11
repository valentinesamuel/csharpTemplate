using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;

/// <summary>
/// <c>Blob:Provider=Local</c>: an <see cref="IBlobStore"/> on the local disk under <c>Blob:Local:RootPath</c>
/// (required; a relative path is resolved against the content root). Development only (D-21).
/// </summary>
public sealed class LocalBlobRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "Local";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Blob;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[LocalBlobOptions.RootPathKey];

        services.AddOptions<LocalBlobOptions>()
            .Configure<IHostEnvironment>((o, environment) => o.RootPath = string.IsNullOrWhiteSpace(configured)
                ? null
                : Path.GetFullPath(configured, environment.ContentRootPath))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.RootPath),
                $"{LocalBlobOptions.RootPathKey} is required when Blob:Provider is {ProviderName}.")
            .ValidateOnStart();

        services.AddSingleton<IBlobStore, LocalDiskBlobStore>();
    }
}
