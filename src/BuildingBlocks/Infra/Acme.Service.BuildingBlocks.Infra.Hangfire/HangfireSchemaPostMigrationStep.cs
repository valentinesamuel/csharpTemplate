using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.BuildingBlocks.Persistence.Migrations;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Acme.Service.BuildingBlocks.Infra.Hangfire;

/// <summary>
/// Installs or upgrades the Hangfire schema (<c>Jobs:Hangfire:Schema</c>, default <c>hangfire</c>) with the installer
/// shipped in Hangfire.PostgreSql, over <c>ConnectionStrings:PostgresMigrations</c> (DDL rights). The only code that
/// creates Hangfire objects (INV-16): registered by <see cref="HangfireRegistration.AddSchemaMigration"/> for the
/// <c>migrate</c> verb when <c>Jobs:Provider=Hangfire</c>. Idempotent: each installer version is applied once and
/// recorded in <c>&lt;schema&gt;.schema</c>; concurrent <c>migrate</c> runs are serialized by the migration lock.
/// </summary>
internal sealed partial class HangfireSchemaPostMigrationStep(
    IConfiguration configuration,
    ILogger<HangfireSchemaPostMigrationStep> logger) : IPostMigrationStep
{
    /// <summary>
    /// Runs after the module migrations (§0.9) and before the Auth permission/role seeder, which uses a higher order.
    /// </summary>
    public const int StepOrder = 100;

    /// <summary>Full key of the connection string used for the install.</summary>
    public const string ConnectionStringKey = "ConnectionStrings:" + ConnectionStringNames.Migrations;

    /// <inheritdoc />
    public string Name => "hangfire-schema";

    /// <inheritdoc />
    public int Order => StepOrder;

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var schema = HangfireOptions.ReadSchema(configuration);
        var failures = new List<string>();
        if (HangfireOptions.ValidateSchema(schema) is { } schemaFailure)
        {
            failures.Add(schemaFailure);
        }

        var connectionString = configuration.GetConnectionString(ConnectionStringNames.Migrations);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            failures.Add($"{ConnectionStringKey} is required to install the Hangfire schema.");
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(nameof(HangfireOptions), typeof(HangfireOptions), failures);
        }

        LogInstalling(logger, schema);
        var connection = new NpgsqlConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // The installer is synchronous; each version runs in its own serializable transaction.
            PostgreSqlObjectsInstaller.Install(connection, schema);
        }

        LogInstalled(logger, schema);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing or upgrading the Hangfire schema {Schema}")]
    private static partial void LogInstalling(ILogger logger, string schema);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hangfire schema {Schema} is up to date")]
    private static partial void LogInstalled(ILogger logger, string schema);
}
