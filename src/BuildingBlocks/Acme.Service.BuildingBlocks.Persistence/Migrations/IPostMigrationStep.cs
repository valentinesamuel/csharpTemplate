namespace Acme.Service.BuildingBlocks.Persistence.Migrations;

/// <summary>
/// Work the <c>migrate</c> command runs after every module context is migrated (for example the Hangfire
/// schema install and the Auth permission/role seeder). Steps are resolved from a scope and run in
/// ascending <see cref="Order"/>, then registration order. Steps must be idempotent (§0.9).
/// Register with <see cref="PersistenceExtensions.AddPostMigrationStep{TStep}"/>.
/// </summary>
public interface IPostMigrationStep
{
    /// <summary>Name used in logs.</summary>
    string Name { get; }

    /// <summary>Relative order; lower runs first.</summary>
    int Order { get; }

    /// <summary>Runs the step.</summary>
    Task ExecuteAsync(CancellationToken cancellationToken);
}
