using System.Collections.Concurrent;

namespace Acme.Service.BuildingBlocks.UseCases;

/// <summary>
/// Maps use-case input types to the key of the module that owns them (D-04). Filled by
/// <see cref="UseCaseRegistrationExtensions.AddUseCasesFromAssembly"/>; read by the transaction behaviour
/// to pick the module's keyed unit of work.
/// </summary>
public sealed class UseCaseModuleRegistry
{
    private readonly ConcurrentDictionary<Type, string> _moduleKeys = new();

    /// <summary>Returns the module key registered for <paramref name="inputType"/>.</summary>
    /// <exception cref="InvalidOperationException">The input type was not registered by any module.</exception>
    public string GetModuleKey(Type inputType)
    {
        ArgumentNullException.ThrowIfNull(inputType);

        return _moduleKeys.TryGetValue(inputType, out var key)
            ? key
            : throw new InvalidOperationException(
                $"Use case input '{inputType.FullName}' is not registered with a module. Call AddUseCasesFromAssembly for its module.");
    }

    internal void Register(Type inputType, string moduleKey)
    {
        var existing = _moduleKeys.GetOrAdd(inputType, moduleKey);
        if (!string.Equals(existing, moduleKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Use case input '{inputType.FullName}' is already registered to module '{existing}'; it cannot also belong to '{moduleKey}'.");
        }
    }
}
