using System.Reflection;

namespace Acme.Service.BuildingBlocks.Web.Modules;

/// <summary>
/// The registered modules in registration order, with the module-name to assembly map (D-04). Use cases
/// of each module assembly are registered under the module name, which is the key
/// <see cref="Persistence.IModuleUnitOfWorkResolver"/> resolves the keyed unit of work with.
/// </summary>
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, Assembly> _assembliesByName;
    private readonly Dictionary<Assembly, string> _namesByAssembly;

    internal ModuleRegistry(IReadOnlyList<IModule> modules)
    {
        _assembliesByName = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        _namesByAssembly = [];

        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module);

            var name = module.Name;
            if (!IsValidName(name))
            {
                throw new ArgumentException(
                    $"Module name '{name}' of '{module.GetType().FullName}' is invalid. Use lower-case letters, digits and underscores, starting with a letter.",
                    nameof(modules));
            }

            var assembly = module.GetType().Assembly;
            if (!_assembliesByName.TryAdd(name, assembly))
            {
                throw new ArgumentException($"Module name '{name}' is registered more than once.", nameof(modules));
            }

            if (!_namesByAssembly.TryAdd(assembly, name))
            {
                throw new ArgumentException(
                    $"Assembly '{assembly.GetName().Name}' already hosts module '{_namesByAssembly[assembly]}'; one module per assembly.",
                    nameof(modules));
            }
        }

        Modules = modules;
    }

    /// <summary>Registered modules in registration order.</summary>
    public IReadOnlyList<IModule> Modules { get; }

    /// <summary>Returns the assembly of the module named <paramref name="moduleName"/>.</summary>
    /// <exception cref="InvalidOperationException">No module has that name.</exception>
    public Assembly GetAssembly(string moduleName) =>
        _assembliesByName.TryGetValue(moduleName, out var assembly)
            ? assembly
            : throw new InvalidOperationException($"No module named '{moduleName}' is registered.");

    /// <summary>Returns the name of the module defined in <paramref name="assembly"/>, or null when none is.</summary>
    public string? FindModuleName(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return _namesByAssembly.GetValueOrDefault(assembly);
    }

    private static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsAsciiLetterLower(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
