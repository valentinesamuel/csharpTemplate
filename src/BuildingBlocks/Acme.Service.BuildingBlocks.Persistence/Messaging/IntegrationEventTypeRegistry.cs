using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Acme.Service.BuildingBlocks.Messaging;

namespace Acme.Service.BuildingBlocks.Persistence.Messaging;

/// <summary>
/// Maps stable integration event names (from <see cref="IntegrationEventAttribute"/>) to CLR types and back
/// (INV-19). Built once from the Contracts assemblies registered with
/// <see cref="MessagingServiceCollectionExtensions.AddIntegrationEvents"/>, plus the module-internal event types
/// registered with <see cref="MessagingServiceCollectionExtensions.AddIntegrationEventTypes"/>. Construction fails when two types
/// declare the same name, or when an event type has no attribute, so a misconfiguration stops startup.
/// </summary>
public sealed class IntegrationEventTypeRegistry
{
    private readonly Dictionary<string, Type> _typesByName;
    private readonly Dictionary<Type, string> _namesByType;

    private IntegrationEventTypeRegistry(Dictionary<string, Type> typesByName)
    {
        _typesByName = typesByName;
        _namesByType = typesByName.ToDictionary(pair => pair.Value, pair => pair.Key);
    }

    /// <summary>The registered event names.</summary>
    public IReadOnlyCollection<string> Names => _typesByName.Keys;

    /// <summary>
    /// Builds the registry from the public event types of <paramref name="assemblies"/>: every concrete type that
    /// implements <see cref="IIntegrationEvent"/> or carries <see cref="IntegrationEventAttribute"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A duplicate name, a missing attribute or an invalid event type.</exception>
    public static IntegrationEventTypeRegistry FromAssemblies(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return FromTypes(ExportedEventTypes(assemblies));
    }

    /// <summary>
    /// The public event types of <paramref name="assemblies"/>: every concrete type that implements
    /// <see cref="IIntegrationEvent"/> or carries <see cref="IntegrationEventAttribute"/>.
    /// </summary>
    internal static IEnumerable<Type> ExportedEventTypes(IEnumerable<Assembly> assemblies) =>
        assemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && (typeof(IIntegrationEvent).IsAssignableFrom(type) || IntegrationEventAttribute.GetName(type) is not null));

    /// <summary>Builds the registry from explicit event types. The same type may appear more than once.</summary>
    /// <exception cref="InvalidOperationException">A duplicate name, a missing attribute or an invalid event type.</exception>
    public static IntegrationEventTypeRegistry FromTypes(IEnumerable<Type> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);

        var typesByName = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in eventTypes.Distinct())
        {
            if (!typeof(IIntegrationEvent).IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters)
            {
                throw new InvalidOperationException(
                    $"Type '{type.FullName}' must be a concrete, non-generic class implementing {nameof(IIntegrationEvent)} to be an integration event.");
            }

            var name = IntegrationEventAttribute.GetName(type)
                ?? throw new InvalidOperationException(
                    $"Integration event '{type.FullName}' has no [IntegrationEvent(\"<module>.<name>.v<n>\")] attribute (INV-19).");

            if (typesByName.TryGetValue(name, out var existing))
            {
                throw new InvalidOperationException(
                    $"Integration event name '{name}' is declared by both '{existing.FullName}' and '{type.FullName}'; names must be unique (INV-19).");
            }

            typesByName.Add(name, type);
        }

        return new IntegrationEventTypeRegistry(typesByName);
    }

    /// <summary>Returns the CLR type registered for <paramref name="name"/>.</summary>
    public bool TryGetType(string name, [NotNullWhen(true)] out Type? eventType) => _typesByName.TryGetValue(name, out eventType);

    /// <summary>Returns the stable name of <paramref name="eventType"/>.</summary>
    /// <exception cref="InvalidOperationException">The type is not registered.</exception>
    public string GetName(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return _namesByType.TryGetValue(eventType, out var name)
            ? name
            : throw new InvalidOperationException(
                $"Integration event '{eventType.FullName}' is not registered. Call AddIntegrationEvents with its Contracts assembly.");
    }
}
