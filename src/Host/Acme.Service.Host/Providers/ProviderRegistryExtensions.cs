namespace Acme.Service.Host.Providers;

/// <summary>Host registration of infrastructure providers (D-02).</summary>
internal static class ProviderRegistryExtensions
{
    /// <summary>
    /// Builds the <see cref="ProviderRegistry"/> from <paramref name="configure"/> (one <c>p.Add&lt;T&gt;()</c> line per
    /// provider), registers the provider selected by <c>&lt;Category&gt;:Provider</c> for every category and logs the
    /// selection at startup. Throws <see cref="Microsoft.Extensions.Options.OptionsValidationException"/> (exit 1 via
    /// Program) when the selection is invalid. Call once.
    /// </summary>
    public static TBuilder AddProviders<TBuilder>(this TBuilder builder, Action<ProviderRegistry> configure)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        if (builder.Services.Any(d => d.ServiceType == typeof(ProviderSelectionReport)))
        {
            throw new InvalidOperationException("AddProviders must be called only once.");
        }

        var registry = new ProviderRegistry();
        configure(registry);

        var selections = registry.Apply(builder.Services, builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(new ProviderSelectionReport(selections));
        builder.Services.AddHostedService<ProviderStartupReporter>();
        return builder;
    }
}

/// <summary>The providers selected at startup.</summary>
/// <param name="Selections">One selection per category.</param>
internal sealed record ProviderSelectionReport(IReadOnlyList<ProviderSelection> Selections);
