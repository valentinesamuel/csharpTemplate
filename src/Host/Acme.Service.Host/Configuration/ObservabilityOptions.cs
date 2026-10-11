namespace Acme.Service.Host.Configuration;

/// <summary>Binds the <c>Observability</c> section (§0.7).</summary>
internal sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>OTel <c>service.name</c>. Required.</summary>
    public string ServiceName { get; set; } = string.Empty;

    public OtlpSettings Otlp { get; set; } = new();

    internal sealed class OtlpSettings
    {
        /// <summary>OTLP gRPC endpoint (for example <c>http://localhost:4317</c>). Export is disabled when empty.</summary>
        public string? Endpoint { get; set; }
    }

    /// <summary>Registers the options with key-named validation and <c>ValidateOnStart</c> (INV-17).</summary>
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ObservabilityOptions>()
            .Bind(configuration.GetSection(SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ServiceName),
                $"{SectionName}:{nameof(ServiceName)} is required.")
            .Validate(
                o => string.IsNullOrWhiteSpace(o.Otlp.Endpoint) || IsHttpUri(o.Otlp.Endpoint),
                $"{SectionName}:{nameof(Otlp)}:{nameof(OtlpSettings.Endpoint)} must be an absolute http(s) URI when set.")
            .ValidateOnStart();
    }

    internal static bool IsHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
