namespace Acme.Service.BuildingBlocks.Persistence.Migrations;

/// <summary>
/// A module context to migrate, registered by <see cref="PersistenceExtensions.AddModuleDbContext{TContext}"/>.
/// Targets are migrated in registration order (§0.9).
/// </summary>
/// <param name="ModuleKey">The module name, equal to the context schema.</param>
/// <param name="ContextType">The <see cref="ModuleDbContext"/> type.</param>
public sealed record MigrationTarget(string ModuleKey, Type ContextType);

/// <summary>Migrations not yet applied to one module context.</summary>
/// <param name="ModuleKey">The module name.</param>
/// <param name="ContextName">The context type name.</param>
/// <param name="Migrations">Pending migration ids, oldest first.</param>
public sealed record PendingMigrations(string ModuleKey, string ContextName, IReadOnlyList<string> Migrations);
