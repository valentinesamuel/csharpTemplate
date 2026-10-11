using Acme.Service.BuildingBlocks.Blob;
using Acme.Service.BuildingBlocks.Providers;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.S3;

/// <summary>
/// <c>Blob:Provider=S3</c>: an <see cref="IBlobStore"/> on Amazon S3 or an S3-compatible store (R2, MinIO, RustFS).
/// Requires <c>Blob:S3:Bucket</c> and <c>Blob:S3:Region</c> or <c>Blob:S3:ServiceUrl</c>; optional
/// <c>Blob:S3:AccessKey</c> + <c>Blob:S3:SecretKey</c> (together; otherwise the default AWS credential chain),
/// <c>Blob:S3:ForcePathStyle</c> (default false) and <c>Blob:S3:CreateIfMissing</c> (default false). Options are
/// validated at startup; startup never contacts S3.
/// </summary>
public sealed class S3BlobRegistration : IProviderRegistration
{
    /// <summary>The provider name.</summary>
    public const string ProviderName = "S3";

    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Blob;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<S3BlobOptions>()
            .Configure(o => S3BlobOptions.Read(configuration, o))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<S3BlobOptions>, S3BlobOptions.Validator>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAmazonS3>(sp => S3BlobStore.CreateClient(sp.GetRequiredService<IOptions<S3BlobOptions>>().Value));
        services.AddSingleton<IBlobStore, S3BlobStore>();
    }
}
