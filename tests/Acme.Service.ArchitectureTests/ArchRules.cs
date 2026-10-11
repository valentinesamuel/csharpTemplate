using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Acme.Service.ArchitectureTests.Support;
using Acme.Service.BuildingBlocks.Messaging;
using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.UseCases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NetArchTest.Rules;

namespace Acme.Service.ArchitectureTests;

/// <summary>
/// The plan P18 architecture rules as reusable functions. Assembly rules take the assemblies to check, so they run
/// against both the real <c>src/</c> assemblies and the deliberately illegal fixture; project and package rules take
/// project files found under the repository root. Type-level dependencies come from NetArchTest (IL inspection via
/// Mono.Cecil); assembly-level references and project files are checked as well because a type scan alone can miss
/// a reference (plan P18 failure modes).
/// </summary>
internal static class ArchRules
{
    /// <summary>Provider namespaces modules and BuildingBlocks core must never use (INV-03).</summary>
    public static readonly IReadOnlyList<string> ProviderNamespaces =
    [
        "StackExchange.Redis",
        "Amazon",
        "Azure",
        "MailKit",
        "MimeKit",
        "Hangfire",
        "Npgsql",
        SolutionAssemblies.InfraPrefix.TrimEnd('.'),
    ];

    private const string EfCoreNamespace = "Microsoft.EntityFrameworkCore";

    private static readonly Regex _integrationEventNamePattern = new(
        @"^(?<module>[a-z][a-z0-9]*)\.(?<name>[a-z0-9]+(?:-[a-z0-9]+)*)\.v(?<version>[1-9][0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// R1 (INV-01): no module implementation assembly depends on another module's implementation assembly; modules
    /// see each other only through <c>*.Contracts</c>. Every non-Contracts assembly passed in is treated as a
    /// separate module and every ordered pair is checked.
    /// </summary>
    public static ArchRuleResult ModulesDependOnlyOnOtherModulesContracts(IReadOnlyCollection<Assembly> assemblies)
    {
        var modules = assemblies
            .Where(a => SolutionAssemblies.Classify(SolutionAssemblies.NameOf(a)) != ProjectKind.ModuleContracts)
            .Distinct()
            .ToList();

        var violations = new List<ArchViolation>();
        foreach (var source in modules)
        {
            foreach (var target in modules.Where(t => t != source))
            {
                violations.AddRange(DependenciesOn(source, target, "R1"));
            }
        }

        return new ArchRuleResult("R1", "a module depends on another module only through its *.Contracts assembly", violations);
    }

    /// <summary>R2 (INV-01): Contracts assemblies reference only BuildingBlocks (core) and the BCL.</summary>
    public static ArchRuleResult ContractsDependOnlyOnBuildingBlocksAndBcl(IReadOnlyCollection<Assembly> contracts)
    {
        var violations = new List<ArchViolation>();
        foreach (var source in contracts)
        {
            foreach (var reference in source.GetReferencedAssemblies())
            {
                var name = reference.Name!;
                if (IsBcl(name) || name == SolutionAssemblies.BuildingBlocksName)
                {
                    continue;
                }

                var target = TryLoad(reference);
                var offenders = target is null ? [] : DependenciesOn(source, target, "R2");
                violations.AddRange(offenders.Count > 0
                    ? offenders
                    : [new ArchViolation(SolutionAssemblies.NameOf(source), $"references assembly '{name}'")]);
            }
        }

        return new ArchRuleResult("R2", "Contracts depend only on BuildingBlocks and the BCL", violations);
    }

    /// <summary>
    /// R3 (INV-03, INV-05): the given assemblies (modules, Contracts, BuildingBlocks core) do not depend on provider
    /// namespaces or on any <c>BuildingBlocks.Infra.*</c> project. The only exemption is the EF-generated migrations
    /// namespace <c>&lt;Assembly&gt;.Persistence.Migrations</c>, whose model snapshots and designer files reference
    /// Npgsql provider metadata (annotations such as <c>NpgsqlValueGenerationStrategy</c>); it is exempt from
    /// <c>Npgsql</c> only.
    /// </summary>
    public static ArchRuleResult NoConcreteProviderDependencies(IReadOnlyCollection<Assembly> assemblies)
    {
        var violations = new List<ArchViolation>();
        foreach (var source in assemblies)
        {
            var sourceName = SolutionAssemblies.NameOf(source);
            var migrationsNamespace = sourceName + ".Persistence.Migrations";

            foreach (var providerNamespace in ProviderNamespaces)
            {
                foreach (var type in TypesDependingOn(source, providerNamespace))
                {
                    if (providerNamespace == "Npgsql" && type.Namespace == migrationsNamespace)
                    {
                        continue;
                    }

                    violations.Add(new ArchViolation(DisplayName(type), $"depends on namespace '{providerNamespace}'"));
                }
            }

            foreach (var reference in source.GetReferencedAssemblies())
            {
                if (reference.Name!.StartsWith(SolutionAssemblies.InfraPrefix, StringComparison.Ordinal))
                {
                    violations.Add(new ArchViolation(sourceName, $"references provider assembly '{reference.Name}'"));
                }
            }
        }

        return new ArchRuleResult("R3", "modules and BuildingBlocks core use no concrete provider types", violations);
    }

    /// <summary>
    /// R4 (INV-04, INV-05): in non-Auth modules, every type depending on <c>Microsoft.EntityFrameworkCore</c> resides
    /// in a <c>*.Persistence*</c> namespace. The Auth module is exempt by assembly name (ADR-0005, INV-05).
    /// </summary>
    public static ArchRuleResult EfCoreConfinedToPersistence(IReadOnlyCollection<Assembly> modules)
    {
        var violations = new List<ArchViolation>();
        foreach (var source in modules.Where(m => !IsAuth(m)))
        {
            foreach (var type in TypesDependingOn(source, EfCoreNamespace))
            {
                if (!IsPersistenceNamespace(type.Namespace))
                {
                    violations.Add(new ArchViolation(
                        DisplayName(type), $"depends on '{EfCoreNamespace}' outside a *.Persistence namespace"));
                }
            }
        }

        return new ArchRuleResult("R4", "EF Core is confined to *.Persistence namespaces (Auth exempt)", violations);
    }

    /// <summary>
    /// INV-06: each module DbContext model maps exactly one schema, equal to the lower-case module name, including
    /// sequences and the migrations history table. Models are built offline through the module's design-time factory
    /// (no database connection is opened).
    /// </summary>
    public static ArchRuleResult ModuleContextsMapSingleSchema(IReadOnlyCollection<Assembly> modules)
    {
        var violations = new List<ArchViolation>();
        foreach (var module in modules)
        {
            var moduleName = SolutionAssemblies.ModuleNameOf(SolutionAssemblies.NameOf(module));
            var expectedSchema = moduleName?.ToLowerInvariant();
            var types = SolutionAssemblies.TypesOf(module);

            var contextTypes = types
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(DbContext).IsAssignableFrom(t))
                .ToList();
            var factories = types
                .Where(t => t is { IsClass: true, IsAbstract: false })
                .SelectMany(t => t.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDesignTimeDbContextFactory<>))
                    .Select(i => (Factory: t, Context: i.GetGenericArguments()[0])))
                .ToList();

            foreach (var contextType in contextTypes)
            {
                if (!typeof(ModuleDbContext).IsAssignableFrom(contextType))
                {
                    violations.Add(new ArchViolation(DisplayName(contextType), "does not derive from ModuleDbContext (INV-06 schema ownership)"));
                }

                var factory = factories.FirstOrDefault(f => f.Context == contextType).Factory;
                if (factory is null)
                {
                    violations.Add(new ArchViolation(
                        DisplayName(contextType), "has no IDesignTimeDbContextFactory, so its model cannot be built offline"));
                    continue;
                }

                using var context = CreateContext(factory, contextType);
                var schemaUses = SchemasOf(context).ToList();
                if (!schemaUses.Any(s => s.Element.StartsWith("table ", StringComparison.Ordinal)))
                {
                    violations.Add(new ArchViolation(DisplayName(contextType), "model maps no tables; the check would pass vacuously"));
                }

                foreach (var schemaUse in schemaUses)
                {
                    if (schemaUse.Schema != expectedSchema)
                    {
                        violations.Add(new ArchViolation(
                            DisplayName(contextType),
                            $"{schemaUse.Element} maps to schema '{schemaUse.Schema ?? "<default>"}', expected '{expectedSchema}'"));
                    }
                }
            }
        }

        return new ArchRuleResult("INV-06", "each module context maps only its own schema", violations);
    }

    /// <summary>
    /// R5 (INV-02): the only public type of a module implementation assembly is <c>&lt;Module&gt;Module</c>. No
    /// design-time factory exemption is applied: EF tools discover internal factories (WH-09), and the factories are
    /// internal.
    /// </summary>
    public static ArchRuleResult OnlyModuleTypeIsPublic(IReadOnlyCollection<Assembly> modules)
    {
        var violations = new List<ArchViolation>();
        foreach (var module in modules)
        {
            var moduleName = SolutionAssemblies.ModuleNameOf(SolutionAssemblies.NameOf(module));
            var allowed = $"{SolutionAssemblies.NameOf(module)}.{moduleName}Module";
            foreach (var type in module.GetExportedTypes())
            {
                if (type.FullName != allowed)
                {
                    violations.Add(new ArchViolation(DisplayName(type), $"is public; only '{allowed}' may be public"));
                }
            }
        }

        return new ArchRuleResult("R5", "the only public type in a module assembly is <Name>Module", violations);
    }

    /// <summary>R6: BuildingBlocks assemblies (core, Web, Persistence, Infra.*) never depend on any module.</summary>
    public static ArchRuleResult BuildingBlocksDoNotDependOnModules(IReadOnlyCollection<Assembly> buildingBlocks)
    {
        var violations = new List<ArchViolation>();
        foreach (var source in buildingBlocks)
        {
            foreach (var type in TypesDependingOn(source, SolutionAssemblies.ModulesNamespace))
            {
                violations.Add(new ArchViolation(DisplayName(type), $"depends on namespace '{SolutionAssemblies.ModulesNamespace}'"));
            }

            foreach (var reference in source.GetReferencedAssemblies())
            {
                if (reference.Name!.StartsWith(SolutionAssemblies.ModulesNamespace + ".", StringComparison.Ordinal))
                {
                    violations.Add(new ArchViolation(SolutionAssemblies.NameOf(source), $"references module assembly '{reference.Name}'"));
                }
            }
        }

        return new ArchRuleResult("R6", "BuildingBlocks do not depend on modules", violations);
    }

    /// <summary>
    /// R7 (INV-19): every integration event type (implements <see cref="IIntegrationEvent"/> or carries the
    /// attribute) declares <c>[IntegrationEvent("&lt;module&gt;.&lt;kebab-name&gt;.v&lt;n&gt;")]</c>; in a module
    /// assembly the module segment equals the lower-case module name; names are unique across all assemblies.
    /// </summary>
    public static ArchRuleResult IntegrationEventsHaveStableUniqueNames(IReadOnlyCollection<Assembly> assemblies)
    {
        var violations = new List<ArchViolation>();
        var owners = new Dictionary<string, List<Type>>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            var moduleName = SolutionAssemblies.ModuleNameOf(SolutionAssemblies.NameOf(assembly));
            var eventTypes = SolutionAssemblies.TypesOf(assembly).Where(t =>
                t is { IsInterface: false, IsAbstract: false }
                && (typeof(IIntegrationEvent).IsAssignableFrom(t) || t.IsDefined(typeof(IntegrationEventAttribute), inherit: false)));

            foreach (var type in eventTypes)
            {
                var name = type.GetCustomAttributesData()
                    .Where(a => a.AttributeType == typeof(IntegrationEventAttribute))
                    .Select(a => a.ConstructorArguments[0].Value as string)
                    .FirstOrDefault();

                if (name is null)
                {
                    violations.Add(new ArchViolation(DisplayName(type), "has no [IntegrationEvent(\"<module>.<kebab-name>.v<n>\")] attribute"));
                    continue;
                }

                var match = _integrationEventNamePattern.Match(name);
                if (!match.Success)
                {
                    violations.Add(new ArchViolation(DisplayName(type), $"event name '{name}' does not match '<module>.<kebab-name>.v<n>'"));
                }
                else if (moduleName is not null && !string.Equals(match.Groups["module"].Value, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(new ArchViolation(
                        DisplayName(type), $"event name '{name}' must start with its module name '{moduleName.ToLowerInvariant()}.'"));
                }

                if (!owners.TryGetValue(name, out var list))
                {
                    owners[name] = list = [];
                }

                list.Add(type);
            }
        }

        foreach (var (name, types) in owners.Where(o => o.Value.Count > 1))
        {
            foreach (var type in types)
            {
                violations.Add(new ArchViolation(
                    DisplayName(type), $"event name '{name}' is also declared by {string.Join(", ", types.Where(t => t != type).Select(DisplayName))}"));
            }
        }

        return new ArchRuleResult("R7", "integration events carry a unique, well-formed [IntegrationEvent] name", violations);
    }

    /// <summary>R8 (INV-02): no <see cref="IUseCase{TIn,TOut}"/> implementation is declared public.</summary>
    public static ArchRuleResult UseCasesAreNotPublic(IReadOnlyCollection<Assembly> assemblies)
    {
        var violations = assemblies
            .SelectMany(SolutionAssemblies.TypesOf)
            .Where(t => (t.IsPublic || t.IsNestedPublic)
                && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IUseCase<,>)))
            .Select(t => new ArchViolation(DisplayName(t), "is a public IUseCase<,> implementation"));

        return new ArchRuleResult("R8", "use cases are not public", violations);
    }

    /// <summary>
    /// R9 (INV-25): no forbidden package (MediatR, AutoMapper, FluentAssertions, Moq, MassTransit v9+) is declared in
    /// <c>Directory.Packages.props</c>, referenced by any project, or resolved (directly or transitively) in any lock file.
    /// </summary>
    public static ArchRuleResult NoForbiddenPackages(string centralPackagesFile, IReadOnlyCollection<ProjectFile> projects, IReadOnlyCollection<string> lockFiles)
    {
        var violations = new List<ArchViolation>();
        var centralVersions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in XDocument.Load(centralPackagesFile).Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
        {
            var id = (string?)item.Attribute("Include");
            if (id is null)
            {
                continue;
            }

            var version = (string?)item.Attribute("Version");
            centralVersions[id] = version;
            if (ForbiddenReason(id, version) is { } reason)
            {
                violations.Add(new ArchViolation(id, $"{reason} (Directory.Packages.props)"));
            }
        }

        foreach (var project in projects)
        {
            foreach (var id in project.PackageReferences)
            {
                if (ForbiddenReason(id, centralVersions.GetValueOrDefault(id)) is { } reason)
                {
                    violations.Add(new ArchViolation(id, $"{reason} (PackageReference in {project.Name})"));
                }
            }
        }

        foreach (var lockFile in lockFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockFile));
            if (!document.RootElement.TryGetProperty("dependencies", out var frameworks))
            {
                continue;
            }

            foreach (var framework in frameworks.EnumerateObject())
            {
                foreach (var package in framework.Value.EnumerateObject())
                {
                    var resolved = package.Value.TryGetProperty("resolved", out var value) ? value.GetString() : null;
                    if (ForbiddenReason(package.Name, resolved) is { } reason)
                    {
                        var relative = Path.GetRelativePath(RepositoryLayout.Root, lockFile);
                        violations.Add(new ArchViolation(package.Name, $"{reason} (resolved in {relative})"));
                    }
                }
            }
        }

        return new ArchRuleResult("R9", "no forbidden or commercially licensed packages", violations);
    }

    /// <summary>R10 (INV-03): among <c>src/</c> projects, only the Host references <c>BuildingBlocks.Infra.*</c> projects.</summary>
    public static ArchRuleResult OnlyHostReferencesInfra(IReadOnlyCollection<ProjectFile> srcProjects)
    {
        var violations = srcProjects
            .Where(p => SolutionAssemblies.Classify(p.Name) != ProjectKind.Host)
            .SelectMany(p => p.ReferencedProjectNames
                .Where(r => SolutionAssemblies.Classify(r) == ProjectKind.Infra)
                .Select(r => new ArchViolation(p.Name, $"references provider project '{r}'; only the Host may")));

        return new ArchRuleResult("R10", "only the Host references Infra.* projects", violations);
    }

    /// <summary>
    /// Plan §0.3 reference matrix over the <c>src/</c> project files: allowed project references per project kind,
    /// BuildingBlocks core packages limited to <c>Microsoft.Extensions.*.Abstractions</c> and FluentValidation, and
    /// Contracts with no package or framework references.
    /// </summary>
    public static ArchRuleResult ProjectReferencesFollowMatrix(IReadOnlyCollection<ProjectFile> srcProjects, string srcDirectory)
    {
        var violations = new List<ArchViolation>();
        foreach (var project in srcProjects)
        {
            var kind = SolutionAssemblies.Classify(project.Name);
            if (kind == ProjectKind.Unknown)
            {
                violations.Add(new ArchViolation(project.Name, "is not a project kind of the plan §0.3 reference matrix"));
                continue;
            }

            foreach (var referencePath in project.ProjectReferences)
            {
                var reference = Path.GetFileNameWithoutExtension(referencePath);
                if (!RepositoryLayout.IsUnder(referencePath, srcDirectory))
                {
                    violations.Add(new ArchViolation(project.Name, $"references '{reference}' outside src/"));
                }
                else if (!IsAllowedReference(project.Name, kind, reference))
                {
                    violations.Add(new ArchViolation(project.Name, $"must not reference '{reference}' ({kind})"));
                }
            }

            if (kind == ProjectKind.BuildingBlocks)
            {
                foreach (var package in project.PackageReferences.Where(p => !IsAllowedCorePackage(p)))
                {
                    violations.Add(new ArchViolation(project.Name, $"must not reference package '{package}'"));
                }

                foreach (var framework in project.FrameworkReferences)
                {
                    violations.Add(new ArchViolation(project.Name, $"must not reference framework '{framework}'"));
                }
            }

            if (kind == ProjectKind.ModuleContracts)
            {
                foreach (var package in project.PackageReferences)
                {
                    violations.Add(new ArchViolation(project.Name, $"must not reference package '{package}'; Contracts depend on BuildingBlocks only"));
                }

                foreach (var framework in project.FrameworkReferences)
                {
                    violations.Add(new ArchViolation(project.Name, $"must not reference framework '{framework}'; Contracts depend on BuildingBlocks only"));
                }
            }
        }

        return new ArchRuleResult("§0.3", "project references follow the reference matrix", violations);
    }

    /// <summary>The project at <paramref name="fixtureName"/> is referenced only by <paramref name="allowedReferrer"/>.</summary>
    public static ArchRuleResult ProjectReferencedOnlyBy(IReadOnlyCollection<ProjectFile> projects, string fixtureName, string allowedReferrer)
    {
        var violations = projects
            .Where(p => p.Name != allowedReferrer && p.ReferencedProjectNames.Contains(fixtureName, StringComparer.Ordinal))
            .Select(p => new ArchViolation(p.Name, $"references '{fixtureName}'; only {allowedReferrer} may"));

        return new ArchRuleResult("Fixture", $"{fixtureName} is referenced only by {allowedReferrer}", violations);
    }

    /// <summary>
    /// Types of <paramref name="source"/> that use any type of <paramref name="target"/>, plus an assembly-level
    /// violation when <paramref name="source"/> references <paramref name="target"/> but no using type was found.
    /// </summary>
    private static List<ArchViolation> DependenciesOn(Assembly source, Assembly target, string ruleId)
    {
        var targetName = SolutionAssemblies.NameOf(target);
        var sourceTypeNames = SolutionAssemblies.TypesOf(source).Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);

        // Top-level type names of the target (nested types match by prefix). Compiler-embedded types such as
        // NullableAttribute exist with the same full name in every assembly, so names the source also defines are skipped.
        var targetTypeNames = SolutionAssemblies.TypesOf(target)
            .Where(t => !t.IsNested && t.FullName is { } name && !name.StartsWith('<') && !sourceTypeNames.Contains(name))
            .Select(t => t.FullName!)
            .ToArray();

        var violations = targetTypeNames.Length == 0
            ? []
            : TypesDependingOnAny(source, targetTypeNames)
                .Select(t => new ArchViolation(DisplayName(t), $"depends on '{targetName}'"))
                .ToList();

        var referencesTarget = source.GetReferencedAssemblies().Any(r => r.Name == targetName);
        if (referencesTarget && violations.Count == 0)
        {
            violations.Add(new ArchViolation(SolutionAssemblies.NameOf(source), $"references assembly '{targetName}' ({ruleId})"));
        }

        return violations;
    }

    private static List<Type> TypesDependingOn(Assembly source, string dependency) => TypesDependingOnAny(source, [dependency]);

    private static List<Type> TypesDependingOnAny(Assembly source, string[] dependencies) =>
        Types.InAssembly(source).That().HaveDependencyOnAny(dependencies).GetTypes().ToList();

    private static Assembly? TryLoad(AssemblyName name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static bool IsBcl(string assemblyName) =>
        assemblyName is "System" or "mscorlib" or "netstandard"
        || assemblyName.StartsWith("System.", StringComparison.Ordinal);

    private static bool IsAuth(Assembly assembly) =>
        SolutionAssemblies.ModuleNameOf(SolutionAssemblies.NameOf(assembly)) == "Auth";

    private static bool IsPersistenceNamespace(string? ns) =>
        ns is not null && ns.Split('.').Contains("Persistence", StringComparer.Ordinal);

    private static bool IsAllowedCorePackage(string id) =>
        id == "FluentValidation"
        || (id.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && id.EndsWith(".Abstractions", StringComparison.Ordinal));

    private static bool IsAllowedReference(string projectName, ProjectKind kind, string reference)
    {
        var referenceKind = SolutionAssemblies.Classify(reference);
        return kind switch
        {
            ProjectKind.BuildingBlocks => false,
            ProjectKind.BuildingBlocksWeb or ProjectKind.BuildingBlocksPersistence or ProjectKind.ModuleContracts =>
                referenceKind == ProjectKind.BuildingBlocks,
            ProjectKind.Infra => referenceKind == ProjectKind.BuildingBlocks
                || (projectName == SolutionAssemblies.InfraHangfireName && referenceKind == ProjectKind.BuildingBlocksPersistence),
            ProjectKind.Module => referenceKind is ProjectKind.BuildingBlocks or ProjectKind.BuildingBlocksWeb
                    or ProjectKind.BuildingBlocksPersistence or ProjectKind.ModuleContracts,
            ProjectKind.Host => referenceKind != ProjectKind.Unknown,
            _ => false,
        };
    }

    private static string? ForbiddenReason(string id, string? version)
    {
        static bool Is(string id, string family) =>
            id.Equals(family, StringComparison.OrdinalIgnoreCase)
            || id.StartsWith(family + ".", StringComparison.OrdinalIgnoreCase);

        foreach (var family in new[] { "MediatR", "AutoMapper", "FluentAssertions", "Moq" })
        {
            if (Is(id, family))
            {
                return $"forbidden package family '{family}'";
            }
        }

        if (Is(id, "MassTransit"))
        {
            var major = MajorVersion(version);
            if (major is null || major >= 9)
            {
                return $"MassTransit v9+ is commercially licensed (version '{version ?? "unknown"}')";
            }
        }

        return null;
    }

    private static int? MajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var digits = new string(version.TrimStart('[', '(', ' ').TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : null;
    }

    private static DbContext CreateContext(Type factoryType, Type contextType)
    {
        var factory = Activator.CreateInstance(factoryType, nonPublic: true)!;
        var create = typeof(IDesignTimeDbContextFactory<>).MakeGenericType(contextType).GetMethod(nameof(IDesignTimeDbContextFactory<DbContext>.CreateDbContext))!;
        return (DbContext)create.Invoke(factory, [Array.Empty<string>()])!;
    }

    private static IEnumerable<(string Element, string? Schema)> SchemasOf(DbContext context)
    {
        var model = context.GetService<IDesignTimeModel>().Model;

        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.GetTableName() is { } table)
            {
                yield return ($"table '{table}' ({entityType.DisplayName()})", entityType.GetSchema());
            }

            if (entityType.GetViewName() is { } view)
            {
                yield return ($"view '{view}' ({entityType.DisplayName()})", entityType.GetViewSchema());
            }
        }

        foreach (var sequence in model.GetSequences())
        {
            yield return ($"sequence '{sequence.Name}'", sequence.Schema);
        }

        var relationalOptions = RelationalOptionsExtension.Extract(context.GetService<IDbContextOptions>());
        yield return ("migrations history table", relationalOptions.MigrationsHistoryTableSchema);
    }

    private static string DisplayName(Type type) => type.FullName ?? type.Name;
}
