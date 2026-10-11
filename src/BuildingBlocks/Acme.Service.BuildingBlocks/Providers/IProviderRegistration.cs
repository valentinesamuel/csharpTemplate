using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.BuildingBlocks.Providers;

/// <summary>
/// Registration entry exposed by each <c>Infra.*</c> project (D-02, INV-24). The Host adds it with one
/// line, and <c>&lt;Category&gt;:Provider</c> equal to <see cref="Name"/> selects it.
/// </summary>
public interface IProviderRegistration
{
    /// <summary>Port family this provider implements.</summary>
    ProviderCategory Category { get; }

    /// <summary>Configuration value that selects this provider (for example <c>Redis</c>).</summary>
    string Name { get; }

    /// <summary>Registers the provider's services and validated options.</summary>
    void Register(IServiceCollection services, IConfiguration configuration);
}
