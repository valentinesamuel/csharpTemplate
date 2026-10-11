using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.S3;

/// <summary>
/// Settings of the S3-compatible blob provider (section <c>Blob:S3</c>; AWS S3, Cloudflare R2, MinIO and similar).
/// Validated at startup (INV-17); validation messages name the key but never echo a credential (INV-28).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AccessKey"/> and <see cref="SecretKey"/> are set together or not at all. When both are absent the
/// AWS SDK default credential chain is used (environment variables, shared profile, web identity / IRSA, ECS or EC2
/// instance roles), which is the recommended setup on AWS. Treat both values as secrets: supply them from the
/// environment or a secret store, never from appsettings.json (INV-11).
/// </para>
/// <para>
/// At least one of <see cref="ServiceUrl"/> (S3-compatible endpoint) and <see cref="Region"/> (AWS region) is
/// required. With a service URL the region, when given, is used as the signing region.
/// </para>
/// </remarks>
public sealed partial class S3BlobOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Blob:S3";

    /// <summary>Full key of <see cref="ServiceUrl"/>.</summary>
    public const string ServiceUrlKey = SectionName + ":ServiceUrl";

    /// <summary>Full key of <see cref="Region"/>.</summary>
    public const string RegionKey = SectionName + ":Region";

    /// <summary>Full key of <see cref="Bucket"/>.</summary>
    public const string BucketKey = SectionName + ":Bucket";

    /// <summary>Full key of <see cref="AccessKey"/>.</summary>
    public const string AccessKeyKey = SectionName + ":AccessKey";

    /// <summary>Full key of <see cref="SecretKey"/>.</summary>
    public const string SecretKeyKey = SectionName + ":SecretKey";

    /// <summary>Full key of <see cref="ForcePathStyle"/>.</summary>
    public const string ForcePathStyleKey = SectionName + ":ForcePathStyle";

    /// <summary>Full key of <see cref="CreateIfMissing"/>.</summary>
    public const string CreateIfMissingKey = SectionName + ":CreateIfMissing";

    private const int MaxRegionLength = 64;

    /// <summary>
    /// Endpoint of an S3-compatible service, for example <c>http://localhost:9000</c> (MinIO) or
    /// <c>https://&lt;account&gt;.r2.cloudflarestorage.com</c> (R2). Leave empty for AWS S3.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>AWS region (for example <c>eu-west-1</c>); the signing region when <see cref="ServiceUrl"/> is set.</summary>
    public string? Region { get; set; }

    /// <summary>Bucket name. Required.</summary>
    public string? Bucket { get; set; }

    /// <summary>Access key id. Secret-adjacent; set together with <see cref="SecretKey"/> or not at all.</summary>
    public string? AccessKey { get; set; }

    /// <summary>Secret access key. A secret; never logged or echoed.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Use path-style addressing (<c>host/bucket/key</c>), required by MinIO and most self-hosted stores. Default false.</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>
    /// Create the bucket at first use when it does not exist. Default false: a missing bucket is a configuration
    /// error. Enable only in development; production buckets are provisioned by infrastructure code.
    /// </summary>
    public bool CreateIfMissing { get; set; }

    /// <summary>Set by <see cref="Read"/> when <see cref="ForcePathStyleKey"/> is not a boolean.</summary>
    internal bool ForcePathStyleInvalid { get; set; }

    /// <summary>Set by <see cref="Read"/> when <see cref="CreateIfMissingKey"/> is not a boolean.</summary>
    internal bool CreateIfMissingInvalid { get; set; }

    /// <summary>The parsed <see cref="ServiceUrl"/>, or null when it is unset or invalid.</summary>
    internal Uri? ServiceUri =>
        Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    /// <summary>True when static credentials are configured (otherwise the default AWS credential chain applies).</summary>
    internal bool HasStaticCredentials => !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>Reads the section from <paramref name="configuration"/>; unparsable booleans are flagged for <see cref="Validator"/>.</summary>
    internal static void Read(IConfiguration configuration, S3BlobOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        options.ServiceUrl = Normalize(configuration[ServiceUrlKey]);
        options.Region = Normalize(configuration[RegionKey]);
        options.Bucket = Normalize(configuration[BucketKey]);
        options.AccessKey = Normalize(configuration[AccessKeyKey]);
        options.SecretKey = Normalize(configuration[SecretKeyKey]);

        (options.ForcePathStyle, options.ForcePathStyleInvalid) = ReadBoolean(configuration[ForcePathStyleKey], options.ForcePathStyle);
        (options.CreateIfMissing, options.CreateIfMissingInvalid) = ReadBoolean(configuration[CreateIfMissingKey], options.CreateIfMissing);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (bool Value, bool Invalid) ReadBoolean(string? value, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (fallback, false);
        }

        return bool.TryParse(value.Trim(), out var parsed) ? (parsed, false) : (fallback, true);
    }

    // S3 bucket naming rules: 3-63 characters, lower-case letters, digits, '.' and '-', starting and ending with a
    // letter or digit. IP-address-shaped names and ".." are rejected separately.
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex BucketPattern();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex IpAddressPattern();

    // AWS region names ("eu-west-1"), R2's "auto" and the custom names some S3-compatible stores use ("garage").
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionPattern();

    /// <summary>Startup validation of <see cref="S3BlobOptions"/>. Never includes a credential in a message.</summary>
    internal sealed class Validator : IValidateOptions<S3BlobOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, S3BlobOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var failures = new List<string>();

            if (options.Bucket is null)
            {
                failures.Add($"{BucketKey} is required when Blob:Provider is {S3BlobRegistration.ProviderName}.");
            }
            else if (!BucketPattern().IsMatch(options.Bucket)
                || options.Bucket.Contains("..", StringComparison.Ordinal)
                || IpAddressPattern().IsMatch(options.Bucket))
            {
                failures.Add($"{BucketKey} must be a valid S3 bucket name (3-63 characters: lower-case letters, digits, '.' and '-').");
            }

            if (options.ServiceUrl is null && options.Region is null)
            {
                failures.Add($"{RegionKey} or {ServiceUrlKey} is required when Blob:Provider is {S3BlobRegistration.ProviderName}.");
            }

            if (options.ServiceUrl is not null && options.ServiceUri is null)
            {
                failures.Add($"{ServiceUrlKey} must be an absolute http or https URL.");
            }

            if (options.Region is not null && (options.Region.Length > MaxRegionLength || !RegionPattern().IsMatch(options.Region)))
            {
                failures.Add($"{RegionKey} must be a region name such as 'eu-west-1' (lower-case letters, digits and '-').");
            }

            var hasAccessKey = options.AccessKey is not null;
            var hasSecretKey = options.SecretKey is not null;
            if (hasAccessKey != hasSecretKey)
            {
                failures.Add(
                    $"{AccessKeyKey} and {SecretKeyKey} must be set together, or both left empty to use the default AWS credential chain.");
            }

            if (options.ForcePathStyleInvalid)
            {
                failures.Add($"{ForcePathStyleKey} must be 'true' or 'false'.");
            }

            if (options.CreateIfMissingInvalid)
            {
                failures.Add($"{CreateIfMissingKey} must be 'true' or 'false'.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
