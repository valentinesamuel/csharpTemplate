using System.Reflection;
using System.Text.RegularExpressions;

namespace Acme.Service.ArchitectureTests.Support;

/// <summary>Kinds of <c>src/</c> projects in the plan §0.3 reference matrix.</summary>
internal enum ProjectKind
{
    Unknown,
    BuildingBlocks,
    BuildingBlocksWeb,
    BuildingBlocksPersistence,
    Infra,
    ModuleContracts,
    Module,
    Host,
}

/// <summary>
/// Classifies project and assembly names and loads the <c>src/</c> assemblies. Modules are discovered from the
/// project files under <c>src/</c>, never hard-coded, so the rules keep working when the sample module is removed.
/// </summary>
internal static class SolutionAssemblies
{
    /// <summary>Root namespace and assembly-name prefix (template token).</summary>
    public const string Root = "Acme.Service";

    public const string BuildingBlocksName = Root + ".BuildingBlocks";
    public const string BuildingBlocksWebName = BuildingBlocksName + ".Web";
    public const string BuildingBlocksPersistenceName = BuildingBlocksName + ".Persistence";
    public const string InfraPrefix = BuildingBlocksName + ".Infra.";
    public const string InfraHangfireName = InfraPrefix + "Hangfire";
    public const string ModulesNamespace = Root + ".Modules";
    public const string HostName = Root + ".Host";

    private static readonly Regex _moduleNamePattern = new(
        "^" + Regex.Escape(ModulesNamespace) + @"\.(?<module>[A-Z][A-Za-z0-9]*)(?<contracts>\.Contracts)?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private static readonly Lazy<IReadOnlyList<Assembly>> _lazySrc = new(LoadSrcAssemblies);

    /// <summary>Every assembly built from a <c>src/</c> project.</summary>
    public static IReadOnlyList<Assembly> Src => _lazySrc.Value;

    /// <summary>Module implementation assemblies (<c>Modules.X</c>, not Contracts).</summary>
    public static IReadOnlyList<Assembly> Modules => Of(ProjectKind.Module);

    /// <summary>Module Contracts assemblies (<c>Modules.X.Contracts</c>).</summary>
    public static IReadOnlyList<Assembly> Contracts => Of(ProjectKind.ModuleContracts);

    /// <summary>Module implementation and Contracts assemblies.</summary>
    public static IReadOnlyList<Assembly> ModulesAndContracts => [.. Modules, .. Contracts];

    /// <summary><c>BuildingBlocks</c> (core) only.</summary>
    public static Assembly BuildingBlocksCore => Of(ProjectKind.BuildingBlocks).Single();

    /// <summary>Every <c>BuildingBlocks*</c> assembly, including Web, Persistence and all Infra providers.</summary>
    public static IReadOnlyList<Assembly> AllBuildingBlocks =>
        [.. Of(ProjectKind.BuildingBlocks), .. Of(ProjectKind.BuildingBlocksWeb), .. Of(ProjectKind.BuildingBlocksPersistence), .. Of(ProjectKind.Infra)];

    public static ProjectKind Classify(string name)
    {
        if (name == HostName)
        {
            return ProjectKind.Host;
        }

        if (name == BuildingBlocksName)
        {
            return ProjectKind.BuildingBlocks;
        }

        if (name == BuildingBlocksWebName)
        {
            return ProjectKind.BuildingBlocksWeb;
        }

        if (name == BuildingBlocksPersistenceName)
        {
            return ProjectKind.BuildingBlocksPersistence;
        }

        if (name.StartsWith(InfraPrefix, StringComparison.Ordinal) && name.Length > InfraPrefix.Length)
        {
            return ProjectKind.Infra;
        }

        var match = ModuleNameRegex().Match(name);
        if (match.Success)
        {
            return match.Groups["contracts"].Success ? ProjectKind.ModuleContracts : ProjectKind.Module;
        }

        return ProjectKind.Unknown;
    }

    /// <summary>The module name (for example <c>Auth</c>) of a module or Contracts assembly, else null.</summary>
    public static string? ModuleNameOf(string assemblyName)
    {
        var match = ModuleNameRegex().Match(assemblyName);
        return match.Success ? match.Groups["module"].Value : null;
    }

    /// <summary>The Contracts assembly name of module <paramref name="moduleName"/>.</summary>
    public static string ContractsNameOf(string moduleName) => $"{ModulesNamespace}.{moduleName}.Contracts";

    public static string NameOf(Assembly assembly) => assembly.GetName().Name!;

    /// <summary>All types of <paramref name="assembly"/>, tolerating types that cannot be loaded.</summary>
    public static IReadOnlyList<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(t => t is not null).ToList()!;
        }
    }

    private static List<Assembly> Of(ProjectKind kind) => Src.Where(a => Classify(NameOf(a)) == kind).ToList();

    private static List<Assembly> LoadSrcAssemblies()
    {
        var assemblies = new List<Assembly>();
        foreach (var project in RepositoryLayout.SrcProjects())
        {
            try
            {
                assemblies.Add(Assembly.Load(new AssemblyName(project.Name)));
            }
            catch (FileNotFoundException exception)
            {
                throw new InvalidOperationException(
                    $"src project '{project.Name}' is not loadable from the test output; the Host must reference every src project (plan §0.3).",
                    exception);
            }
        }

        return assemblies;
    }

    // Built from the token constant (not a literal pattern) so the template init script renames it with the rest.
    private static Regex ModuleNameRegex() => _moduleNamePattern;
}
