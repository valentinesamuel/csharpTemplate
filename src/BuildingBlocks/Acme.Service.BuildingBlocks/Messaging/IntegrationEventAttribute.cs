using System.Reflection;
using System.Text.RegularExpressions;

namespace Acme.Service.BuildingBlocks.Messaging;

/// <summary>
/// Declares the stable persisted name of an integration event, <c>&lt;module&gt;.&lt;kebab-name&gt;.v&lt;n&gt;</c>
/// (for example <c>orders.order-placed.v1</c>). Persisted type names come from this attribute, never from
/// CLR type names (INV-19).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed partial class IntegrationEventAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="name">Event name matching <c>^[a-z0-9-]+\.[a-z0-9-]+\.v\d+$</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> does not match the required format.</exception>
    public IntegrationEventAttribute(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException(
                $"Integration event name '{name}' must match '<module>.<kebab-name>.v<n>'.", nameof(name));
        }

        Name = name;
    }

    /// <summary>The stable event name.</summary>
    public string Name { get; }

    /// <summary>True when <paramref name="name"/> matches <c>^[a-z0-9-]+\.[a-z0-9-]+\.v\d+$</c>.</summary>
    public static bool IsValidName(string? name) => name is not null && NameRegex().IsMatch(name);

    /// <summary>Returns the declared name of <paramref name="eventType"/>, or null when it has no attribute.</summary>
    public static string? GetName(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return eventType.GetCustomAttribute<IntegrationEventAttribute>(inherit: false)?.Name;
    }

    // ASCII digits and \z (not $) so that a trailing newline is not accepted.
    [GeneratedRegex(@"^[a-z0-9-]+\.[a-z0-9-]+\.v[0-9]+\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NameRegex();
}
