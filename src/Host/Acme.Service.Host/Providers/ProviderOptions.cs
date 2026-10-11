using Acme.Service.BuildingBlocks.Providers;

namespace Acme.Service.Host.Providers;

/// <summary>
/// The provider selected for one <see cref="ProviderCategory"/>, read from <c>&lt;Category&gt;:Provider</c> (§0.7).
/// Registered as named options (name = category, for example <c>Cache</c>) after <see cref="ProviderRegistry"/> has
/// validated the selection, so later code can read which provider is active.
/// </summary>
internal sealed class ProviderOptions
{
    /// <summary>The key inside each category section.</summary>
    public const string ProviderKey = "Provider";

    /// <summary>The selected provider name, as declared by its registration (for example <c>InMemory</c>).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The configuration key of <paramref name="category"/>'s provider, for example <c>Cache:Provider</c>.</summary>
    public static string KeyFor(ProviderCategory category) => $"{category}:{ProviderKey}";

    /// <summary>Registers the selection for <paramref name="category"/>, validated on start (INV-17).</summary>
    public static void Register(IServiceCollection services, ProviderCategory category, string provider)
    {
        services.AddOptions<ProviderOptions>(category.ToString())
            .Configure(o => o.Provider = provider)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Provider), $"{KeyFor(category)} is required.")
            .ValidateOnStart();
    }
}
