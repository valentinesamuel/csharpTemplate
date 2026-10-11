using System.Net;

namespace Acme.Service.Host.Configuration;

/// <summary>Binds the <c>Http</c> section (§0.7).</summary>
internal sealed class HttpOptions
{
    public const string SectionName = "Http";

    public ForwardedHeadersSettings ForwardedHeaders { get; set; } = new();

    internal sealed class ForwardedHeadersSettings
    {
        /// <summary>Trusted proxy networks in CIDR notation (for example <c>10.0.0.0/8</c>).</summary>
        public ICollection<string> KnownNetworks { get; } = new List<string>();

        /// <summary>Trusted proxy IP addresses.</summary>
        public ICollection<string> KnownProxies { get; } = new List<string>();
    }

    /// <summary>Registers the options with key-named validation and <c>ValidateOnStart</c> (INV-17).</summary>
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        const string prefix = $"{SectionName}:{nameof(ForwardedHeaders)}";

        services.AddOptions<HttpOptions>()
            .Bind(configuration.GetSection(SectionName))
            .Validate(
                o => o.ForwardedHeaders.KnownNetworks.All(n => IPNetwork.TryParse(n, out _)),
                $"{prefix}:{nameof(ForwardedHeadersSettings.KnownNetworks)} entries must be CIDR networks (for example 10.0.0.0/8).")
            .Validate(
                o => o.ForwardedHeaders.KnownProxies.All(p => IPAddress.TryParse(p, out _)),
                $"{prefix}:{nameof(ForwardedHeadersSettings.KnownProxies)} entries must be IP addresses.")
            .ValidateOnStart();
    }
}
