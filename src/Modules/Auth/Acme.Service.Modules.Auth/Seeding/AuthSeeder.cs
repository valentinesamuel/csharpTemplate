using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Acme.Service.BuildingBlocks.Web.Modules;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acme.Service.Modules.Auth.Seeding;

/// <summary>
/// Re-syncs the code-owned permission catalog and seed roles on every <c>migrate</c> run (§0.8, §0.9):
/// <list type="number">
/// <item>upserts every <see cref="IModule.Permissions"/> entry of every registered module into <c>auth.permissions</c>;</item>
/// <item>deletes permissions no longer declared (their grants cascade);</item>
/// <item>ensures the seed roles Admin, Staff and Customer exist;</item>
/// <item>sets each seed role's permissions to exactly the declared defaults.</item>
/// </list>
/// Idempotent. Runs in one transaction under a transaction-scoped Postgres advisory lock, so concurrent runs (two
/// migrate jobs) are serialized even without the migration runner's lock. Users' role memberships are never touched.
/// </summary>
internal sealed partial class AuthSeeder(
    AuthDbContext context,
    ModuleRegistry modules,
    ILookupNormalizer normalizer,
    ILogger<AuthSeeder> logger) : IPostMigrationStep
{
    /// <summary>After the module migrations and the Hangfire schema step (Order 100).</summary>
    public const int StepOrder = 200;

    /// <summary>Advisory lock name; hashed server-side with <c>hashtextextended</c>.</summary>
    public const string LockName = "auth.seed";

    public string Name => "auth-seed";

    public int Order => StepOrder;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var catalog = PermissionCatalog.Build(modules.Modules);

        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // Held until commit or rollback; a second seeder waits here, then sees the first one's committed rows.
            await context.Database
                .ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({LockName}, 0))", cancellationToken)
                .ConfigureAwait(false);

            var changes = await SyncPermissionsAsync(catalog, cancellationToken).ConfigureAwait(false);
            changes += await SyncRolesAsync(catalog, cancellationToken).ConfigureAwait(false);

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            LogSeeded(logger, catalog.Entries.Count, AuthRole.SeedRoles.Count, changes);
        }
    }

    private async Task<int> SyncPermissionsAsync(PermissionCatalog catalog, CancellationToken cancellationToken)
    {
        var existing = await context.Permissions.ToDictionaryAsync(p => p.Name, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var changes = 0;

        foreach (var entry in catalog.Entries)
        {
            var definition = entry.Definition;
            if (!existing.Remove(definition.Name, out var permission))
            {
                context.Permissions.Add(new Permission(definition.Name, definition.Description, entry.Module));
                changes++;
            }
            else if (permission.Description != definition.Description || permission.Module != entry.Module)
            {
                permission.Description = definition.Description;
                permission.Module = entry.Module;
                changes++;
            }
        }

        // Whatever is left is no longer declared by any module.
        foreach (var undeclared in existing.Values)
        {
            LogRemovingPermission(logger, undeclared.Name);
            context.Permissions.Remove(undeclared);
            changes++;
        }

        return changes;
    }

    private async Task<int> SyncRolesAsync(PermissionCatalog catalog, CancellationToken cancellationToken)
    {
        var normalizedNames = AuthRole.SeedRoles.Select(r => normalizer.NormalizeName(r)).ToList();
        var roles = await context.Roles
            .Include(r => r.Permissions)
            .Where(r => normalizedNames.Contains(r.NormalizedName!))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var changes = 0;

        foreach (var roleName in AuthRole.SeedRoles)
        {
            var normalized = normalizer.NormalizeName(roleName);
            var role = roles.SingleOrDefault(r => r.NormalizedName == normalized);
            if (role is null)
            {
                role = new AuthRole(roleName)
                {
                    NormalizedName = normalized,
                    ConcurrencyStamp = Guid.NewGuid().ToString(),
                };
                context.Roles.Add(role);
                LogCreatingRole(logger, roleName);
                changes++;
            }

            var desired = catalog.DefaultPermissionsOf(roleName);
            foreach (var grant in role.Permissions.Where(p => !desired.Contains(p.PermissionName)).ToList())
            {
                role.Permissions.Remove(grant);
                changes++;
            }

            var granted = role.Permissions.Select(p => p.PermissionName).ToHashSet(StringComparer.Ordinal);
            foreach (var name in desired.Where(n => !granted.Contains(n)).Order(StringComparer.Ordinal))
            {
                role.Permissions.Add(new RolePermission(role.Id, name));
                changes++;
            }
        }

        return changes;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Auth seed complete: {PermissionCount} permission(s), {RoleCount} seed role(s), {ChangeCount} change(s)")]
    private static partial void LogSeeded(ILogger logger, int permissionCount, int roleCount, int changeCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removing undeclared permission {Permission}")]
    private static partial void LogRemovingPermission(ILogger logger, string permission);

    [LoggerMessage(Level = LogLevel.Information, Message = "Creating seed role {Role}")]
    private static partial void LogCreatingRole(ILogger logger, string role);
}
