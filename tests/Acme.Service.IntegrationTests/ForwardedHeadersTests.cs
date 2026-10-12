using Acme.Service.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Acme.Service.IntegrationTests;

/// <summary>
/// Forwarded-header trust (<c>Http:ForwardedHeaders</c>): loopback only by default, and any sender, nearest hop only,
/// when <c>TrustAllProxies</c> is set for platforms with dynamic proxy addresses.
/// </summary>
public sealed class ForwardedHeadersTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void Options_Default_TrustsLoopbackOnly()
    {
        using var factory = new ServiceWebFactory();

        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.KnownProxies.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Options_TrustAllProxies_ClearsTrustListsAndLimitsToOneHop()
    {
        using var factory = new TrustAllProxiesFactory();

        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.KnownProxies.ShouldBeEmpty();
        options.KnownIPNetworks.ShouldBeEmpty();
        options.ForwardLimit.ShouldBe(1);
    }

    private sealed class TrustAllProxiesFactory : ServiceWebFactory
    {
        protected override IReadOnlyDictionary<string, string?> ConfigurationOverrides { get; } =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Http:ForwardedHeaders:TrustAllProxies"] = "true",
            };
    }
}
