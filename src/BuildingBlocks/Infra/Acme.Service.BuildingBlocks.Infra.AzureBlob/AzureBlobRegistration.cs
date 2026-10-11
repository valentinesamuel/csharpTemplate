using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Providers;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.AzureBlob;

/// <summary>
/// <c>Blob:Provider=AzureBlob</c>: an <see cref="IBlobStore"/> on Azure Blob Storage. Requires
/// <c>Blob:AzureBlob:Container</c> and exactly one of <c>Blob:AzureBlob:ConnectionString</c> (shared key) and
/// <c>Blob:AzureBlob:ServiceUri</c> (<c>DefaultAzureCredential</c>); optional <c>Blob:AzureBlob:CreateIfMissing</c>
/// (default false). Options are validated at startup; startup never contacts Azure.
/// </summary>
public sealed class AzureBlobRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "AzureBlob";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Blob;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AzureBlobOptions>()
            .Configure(o => AzureBlobOptions.Read(configuration, o))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AzureBlobOptions>, AzureBlobOptions.Validator>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => AzureBlobStore.CreateClient(sp.GetRequiredService<IOptions<AzureBlobOptions>>().Value));
        services.AddSingleton<IBlobStore, AzureBlobStore>();
    }
}
