using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.AzureBlob;

/// <summary>
/// Settings of the Azure Blob Storage provider (section <c>Blob:AzureBlob</c>). Validated at startup (INV-17);
/// validation messages name the key but never echo the connection string (INV-28).
/// </summary>
/// <remarks>
/// Exactly one of <see cref="ConnectionString"/> and <see cref="ServiceUri"/> is set:
/// <list type="bullet">
/// <item><see cref="ConnectionString"/>: shared-key authentication; download URLs are service SAS URLs. A secret:
/// supply it from the environment or a secret store, never from appsettings.json (INV-11).</item>
/// <item><see cref="ServiceUri"/> (for example <c>https://&lt;account&gt;.blob.core.windows.net</c>): Microsoft Entra
/// ID authentication through <c>DefaultAzureCredential</c> (workload identity, managed identity, environment, Azure
/// CLI); download URLs are user delegation SAS URLs. The identity needs a data-plane role such as
/// <em>Storage Blob Data Contributor</em> (plus <em>Storage Blob Delegator</em> at account scope when not
/// already granted by it).</item>
/// </list>
/// </remarks>
public sealed partial class AzureBlobOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Blob:AzureBlob";

    /// <summary>Full key of <see cref="ConnectionString"/>.</summary>
    public const string ConnectionStringKey = SectionName + ":ConnectionString";

    /// <summary>Full key of <see cref="ServiceUri"/>.</summary>
    public const string ServiceUriKey = SectionName + ":ServiceUri";

    /// <summary>Full key of <see cref="Container"/>.</summary>
    public const string ContainerKey = SectionName + ":Container";

    /// <summary>Full key of <see cref="CreateIfMissing"/>.</summary>
    public const string CreateIfMissingKey = SectionName + ":CreateIfMissing";

    /// <summary>Storage connection string (shared key). A secret; never logged or echoed.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Blob service endpoint used with <c>DefaultAzureCredential</c>.</summary>
    public string? ServiceUri { get; set; }

    /// <summary>Container name. Required.</summary>
    public string? Container { get; set; }

    /// <summary>
    /// Create the container at first use when it does not exist. Default false: a missing container is a
    /// configuration error. Enable only in development; production containers are provisioned by infrastructure code.
    /// </summary>
    public bool CreateIfMissing { get; set; }

    /// <summary>Set by <see cref="Read"/> when <see cref="CreateIfMissingKey"/> is not a boolean.</summary>
    internal bool CreateIfMissingInvalid { get; set; }

    /// <summary>The parsed <see cref="ServiceUri"/>, or null when it is unset or invalid.</summary>
    internal Uri? ParsedServiceUri =>
        Uri.TryCreate(ServiceUri, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    /// <summary>Reads the section from <paramref name="configuration"/>; an unparsable boolean is flagged for <see cref="Validator"/>.</summary>
    internal static void Read(IConfiguration configuration, AzureBlobOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        options.ConnectionString = Normalize(configuration[ConnectionStringKey]);
        options.ServiceUri = Normalize(configuration[ServiceUriKey]);
        options.Container = Normalize(configuration[ContainerKey]);

        var createIfMissing = configuration[CreateIfMissingKey];
        if (!string.IsNullOrWhiteSpace(createIfMissing))
        {
            if (bool.TryParse(createIfMissing.Trim(), out var parsed))
            {
                options.CreateIfMissing = parsed;
            }
            else
            {
                options.CreateIfMissingInvalid = true;
            }
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Container naming rules: 3-63 characters, lower-case letters, digits and single '-' between them.
    [GeneratedRegex("^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerPattern();

    /// <summary>Startup validation of <see cref="AzureBlobOptions"/>. Never includes the connection string in a message.</summary>
    internal sealed class Validator : IValidateOptions<AzureBlobOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, AzureBlobOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var failures = new List<string>();

            if (options.ConnectionString is null && options.ServiceUri is null)
            {
                failures.Add(
                    $"Exactly one of {ConnectionStringKey} and {ServiceUriKey} is required when Blob:Provider is {AzureBlobRegistration.ProviderName}; neither is set.");
            }
            else if (options.ConnectionString is not null && options.ServiceUri is not null)
            {
                failures.Add($"Exactly one of {ConnectionStringKey} and {ServiceUriKey} may be set; both are set.");
            }
            else if (options.ConnectionString is not null && !IsValidConnectionString(options.ConnectionString))
            {
                // Never echo the value: it contains the account key (INV-28).
                failures.Add($"{ConnectionStringKey} is not a valid Azure Storage connection string.");
            }
            else if (options.ServiceUri is not null && options.ParsedServiceUri is null)
            {
                failures.Add($"{ServiceUriKey} must be an absolute http or https URL such as https://<account>.blob.core.windows.net.");
            }

            if (options.Container is null)
            {
                failures.Add($"{ContainerKey} is required when Blob:Provider is {AzureBlobRegistration.ProviderName}.");
            }
            else if (!ContainerPattern().IsMatch(options.Container))
            {
                failures.Add($"{ContainerKey} must be a valid container name (3-63 characters: lower-case letters, digits and single '-').");
            }

            if (options.CreateIfMissingInvalid)
            {
                failures.Add($"{CreateIfMissingKey} must be 'true' or 'false'.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }

        private static bool IsValidConnectionString(string connectionString)
        {
            try
            {
                // Parses only; no network I/O.
                _ = new BlobServiceClient(connectionString);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }
    }
}
