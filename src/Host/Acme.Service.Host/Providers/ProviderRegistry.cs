using Acme.Service.BuildingBlocks.Infra.InMemory.Registrations;
using Acme.Service.BuildingBlocks.Providers;
using Microsoft.Extensions.Options;

namespace Acme.Service.Host.Providers;

/// <summary>
/// The provider registry (D-02, INV-24). Infra projects expose one <see cref="IProviderRegistration"/> per
/// provider; the Host adds each with one line and <c>&lt;Category&gt;:Provider</c> selects exactly one per category.
/// </summary>
/// <remarks>
/// <see cref="Apply"/> fails startup (INV-17), reporting every problem at once with the config key named, when a
/// category's value is missing, matches no registration, matches more than one registration, or selects a
/// development-only provider in the Production environment (D-21). Names match case-insensitively.
/// </remarks>
internal sealed class ProviderRegistry
{
    /// <summary>
    /// Providers rejected in the Production environment (D-21). <c>Cache:Provider=InMemory</c> is deliberately absent:
    /// it is allowed, with a warning when idempotency is enabled (see <see cref="ProviderStartupReporter"/>).
    /// </summary>
    private static readonly (ProviderCategory Category, string Name)[] _developmentOnly =
    [
        (ProviderCategory.Blob, LocalBlobRegistration.ProviderName),
        (ProviderCategory.Notifications, ConsoleNotificationsRegistration.ProviderName),
        (ProviderCategory.Jobs, InMemoryJobsRegistration.ProviderName),
        (ProviderCategory.Locks, InMemoryLockRegistration.ProviderName),
    ];

    private readonly List<IProviderRegistration> _registrations = [];

    /// <summary>The registrations added so far, in order.</summary>
    public IReadOnlyList<IProviderRegistration> Registrations => _registrations;

    /// <summary>Adds the registration <typeparamref name="TRegistration"/>.</summary>
    public ProviderRegistry Add<TRegistration>()
        where TRegistration : IProviderRegistration, new() =>
        Add(new TRegistration());

    /// <summary>Adds <paramref name="registration"/>.</summary>
    public ProviderRegistry Add(IProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!Enum.IsDefined(registration.Category) || string.IsNullOrWhiteSpace(registration.Name))
        {
            throw new ArgumentException(
                $"Provider registration {registration.GetType().Name} must declare a known category and a name.",
                nameof(registration));
        }

        _registrations.Add(registration);
        return this;
    }

    /// <summary>True when <paramref name="name"/> is a development-only provider of <paramref name="category"/> (D-21).</summary>
    public static bool IsDevelopmentOnly(ProviderCategory category, string name) =>
        _developmentOnly.Any(d => d.Category == category && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Selects one registration per category from <c>&lt;Category&gt;:Provider</c> and calls its
    /// <see cref="IProviderRegistration.Register"/>. Nothing is registered unless every category is valid.
    /// </summary>
    /// <returns>The selected registration per category, in <see cref="ProviderCategory"/> order.</returns>
    /// <exception cref="OptionsValidationException">The selection is invalid; the failures name each config key.</exception>
    public IReadOnlyList<ProviderSelection> Apply(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var failures = new List<string>();
        var selections = new List<ProviderSelection>();

        foreach (var category in Enum.GetValues<ProviderCategory>())
        {
            var key = ProviderOptions.KeyFor(category);
            var value = configuration[key]?.Trim();
            var candidates = _registrations.Where(r => r.Category == category).ToList();
            var registered = candidates.Count == 0
                ? "none"
                : string.Join(", ", candidates.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal));

            if (string.IsNullOrEmpty(value))
            {
                failures.Add($"{key} is required. Registered providers: {registered}.");
                continue;
            }

            var matches = candidates.Where(r => string.Equals(r.Name, value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                failures.Add($"{key} value '{value}' does not match a registered provider. Registered providers: {registered}.");
            }
            else if (matches.Count > 1)
            {
                failures.Add(
                    $"{key} value '{value}' matches {matches.Count} registrations ({string.Join(", ", matches.Select(m => m.GetType().Name))}); " +
                    "exactly one registration per provider name is allowed.");
            }
            else if (environment.IsProduction() && IsDevelopmentOnly(category, matches[0].Name))
            {
                failures.Add($"{key}={matches[0].Name} is a development-only provider and is not allowed in the Production environment (D-21).");
            }
            else
            {
                selections.Add(new ProviderSelection(category, matches[0]));
            }
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(nameof(ProviderOptions), typeof(ProviderOptions), failures);
        }

        foreach (var selection in selections)
        {
            selection.Registration.Register(services, configuration);
            ProviderOptions.Register(services, selection.Category, selection.Registration.Name);
        }

        return selections;
    }
}

/// <summary>The registration selected for a category.</summary>
/// <param name="Category">The port family.</param>
/// <param name="Registration">The selected registration.</param>
internal sealed record ProviderSelection(ProviderCategory Category, IProviderRegistration Registration);
