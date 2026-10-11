using Acme.Service.BuildingBlocks.Security;
using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Modules.Auth.Contracts;
using Acme.Service.Modules.Auth.Domain;

namespace Acme.Service.Modules.Auth.Seeding;

/// <summary>
/// The code-owned permission catalog (plan §0.8): the permissions declared by every registered module, each with
/// its owner module and seed roles. Built and validated before the seeder writes anything.
/// </summary>
internal sealed class PermissionCatalog
{
    /// <summary>The permissions declared by the Auth module.</summary>
    public static readonly IReadOnlyList<PermissionDefinition> AuthPermissionDefinitions =
    [
        new(AuthPermissions.UsersRead, "Read users.", [AuthRole.Admin, AuthRole.Staff]),
        new(AuthPermissions.UsersManage, "Manage users: assign and unassign roles, lock and unlock accounts.", [AuthRole.Admin]),
        new(AuthPermissions.RolesRead, "Read roles.", [AuthRole.Admin, AuthRole.Staff]),
    ];

    private PermissionCatalog(IReadOnlyList<Entry> entries)
    {
        Entries = entries;
    }

    /// <summary>The declared permissions, in module registration then declaration order.</summary>
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>The permission names granted to <paramref name="roleName"/> by default.</summary>
    public IReadOnlySet<string> DefaultPermissionsOf(string roleName) =>
        Entries.Where(e => e.Definition.DefaultRoles.Contains(roleName, StringComparer.Ordinal))
            .Select(e => e.Definition.Name)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Builds the catalog from <paramref name="modules"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// A name is malformed or declared twice, a field is too long, or a default role is not a seed role.
    /// </exception>
    public static PermissionCatalog Build(IEnumerable<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var entries = new List<Entry>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var module in modules)
        {
            foreach (var definition in module.Permissions)
            {
                if (!IsValidName(definition.Name))
                {
                    errors.Add($"Permission '{definition.Name}' of module '{module.Name}' must be lower-case dotted '<resource>.<action>' (at most {Permission.NameMaxLength} characters).");
                    continue;
                }

                if (!owners.TryAdd(definition.Name, module.Name))
                {
                    errors.Add($"Permission '{definition.Name}' is declared by both '{owners[definition.Name]}' and '{module.Name}'.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(definition.Description) || definition.Description.Length > Permission.DescriptionMaxLength)
                {
                    errors.Add($"Permission '{definition.Name}' needs a description of at most {Permission.DescriptionMaxLength} characters.");
                }

                foreach (var role in definition.DefaultRoles.Where(r => !AuthRole.SeedRoles.Contains(r, StringComparer.Ordinal)))
                {
                    errors.Add($"Permission '{definition.Name}' names default role '{role}', which is not a seed role ({string.Join(", ", AuthRole.SeedRoles)}).");
                }

                entries.Add(new Entry(definition, module.Name));
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid permission catalog: " + string.Join(" ", errors));
        }

        return new PermissionCatalog(entries);
    }

    private static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > Permission.NameMaxLength)
        {
            return false;
        }

        var segments = name.Split('.');
        return segments.Length >= 2 && segments.All(IsValidSegment);
    }

    private static bool IsValidSegment(string segment) =>
        segment.Length > 0
        && char.IsAsciiLetterLower(segment[0])
        && segment.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-' || c == '_');

    /// <summary>A declared permission and the module that owns it.</summary>
    internal sealed record Entry(PermissionDefinition Definition, string Module);
}
