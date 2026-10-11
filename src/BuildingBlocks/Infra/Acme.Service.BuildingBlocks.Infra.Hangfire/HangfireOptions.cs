using System.Globalization;
using System.Text.RegularExpressions;
using Acme.Service.BuildingBlocks.Persistence;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Acme.Service.BuildingBlocks.Infra.Hangfire;

/// <summary>
/// Settings of the Hangfire job provider (section <c>Jobs:Hangfire</c>, storage on <c>ConnectionStrings:Postgres</c>).
/// Validated at startup (INV-17); messages name the full key and never echo the connection string (INV-28).
/// </summary>
/// <remarks>
/// The storage never creates or upgrades its schema (<c>PrepareSchemaIfNecessary=false</c>, INV-16): the schema is
/// installed only by the <c>migrate</c> verb (<see cref="HangfireSchemaPostMigrationStep"/>). When it is missing, the
/// web app still starts and stays ready; Hangfire workers log errors until <c>migrate</c> has run.
/// </remarks>
public sealed partial class HangfireOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Jobs:Hangfire";

    /// <summary>Full key of <see cref="Schema"/>.</summary>
    public const string SchemaKey = SectionName + ":Schema";

    /// <summary>Full key of <see cref="WorkerCount"/>.</summary>
    public const string WorkerCountKey = SectionName + ":WorkerCount";

    /// <summary>Full key of <see cref="DashboardEnabled"/>.</summary>
    public const string DashboardEnabledKey = SectionName + ":DashboardEnabled";

    /// <summary>Full key of the storage connection string (the app's DML connection, §0.7).</summary>
    public const string ConnectionStringKey = "ConnectionStrings:" + ConnectionStringNames.App;

    /// <summary>Default schema (§0.5: the database has the module schemas plus <c>hangfire</c>).</summary>
    public const string DefaultSchema = "hangfire";

    /// <summary>Upper bound of <see cref="WorkerCount"/>.</summary>
    public const int MaxWorkerCount = 1000;

    /// <summary>Hangfire's own default polling interval for queues and delayed/recurring jobs.</summary>
    public static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(15);

    /// <summary>Postgres schema of the Hangfire tables. Default <c>hangfire</c>.</summary>
    public string Schema { get; set; } = DefaultSchema;

    /// <summary>Concurrent workers per server (replica). Default: Hangfire's, <c>min(5 x processors, 20)</c>.</summary>
    public int WorkerCount { get; set; } = Math.Min(Environment.ProcessorCount * 5, 20);

    /// <summary>
    /// Map the Hangfire dashboard at <see cref="HangfireDashboardExtensions.DashboardPath"/>. Default false. Honoured
    /// only in the Development environment (D-21); ignored with a warning elsewhere.
    /// </summary>
    public bool DashboardEnabled { get; set; }

    /// <summary>The storage connection string (<c>ConnectionStrings:Postgres</c>). A secret; never logged or echoed.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// How often idle workers poll the queue table. Code-level setting, not bound from configuration (§0.7 defines no
    /// key); tests shorten it. Default 15 s.
    /// </summary>
    public TimeSpan QueuePollInterval { get; set; } = DefaultPollingInterval;

    /// <summary>
    /// How often scheduled and recurring jobs are checked; also the effective resolution of <c>ScheduleAsync</c> delays
    /// and of sub-minute cron expressions. Code-level setting, not bound from configuration. Default 15 s.
    /// </summary>
    public TimeSpan SchedulePollingInterval { get; set; } = DefaultPollingInterval;

    /// <summary>Set by <see cref="Read"/> when <see cref="WorkerCountKey"/> is not an integer.</summary>
    internal bool WorkerCountInvalid { get; set; }

    /// <summary>Set by <see cref="Read"/> when <see cref="DashboardEnabledKey"/> is not a boolean.</summary>
    internal bool DashboardEnabledInvalid { get; set; }

    /// <summary>
    /// Builds the storage options. <c>PrepareSchemaIfNecessary</c> is always false: the web app never creates the
    /// schema (INV-16).
    /// </summary>
    internal PostgreSqlStorageOptions ToStorageOptions() => new()
    {
        SchemaName = Schema,
        PrepareSchemaIfNecessary = false,
        QueuePollInterval = QueuePollInterval,
    };

    /// <summary>Reads the section and the connection string; unparsable values are flagged for <see cref="Validator"/>.</summary>
    internal static void Read(IConfiguration configuration, HangfireOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        options.Schema = ReadSchema(configuration);
        var connectionString = configuration.GetConnectionString(ConnectionStringNames.App);
        options.ConnectionString = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString;

        var workerCount = Normalize(configuration[WorkerCountKey]);
        if (workerCount is not null)
        {
            options.WorkerCountInvalid = !int.TryParse(workerCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed);
            options.WorkerCount = options.WorkerCountInvalid ? options.WorkerCount : parsed;
        }

        var dashboardEnabled = Normalize(configuration[DashboardEnabledKey]);
        if (dashboardEnabled is not null)
        {
            options.DashboardEnabledInvalid = !bool.TryParse(dashboardEnabled, out var parsed);
            options.DashboardEnabled = !options.DashboardEnabledInvalid && parsed;
        }
    }

    /// <summary>The configured schema (<see cref="SchemaKey"/>), or <see cref="DefaultSchema"/> when unset. Not validated.</summary>
    internal static string ReadSchema(IConfiguration configuration) =>
        Normalize(configuration[SchemaKey]) ?? DefaultSchema;

    /// <summary>
    /// The validation message for <paramref name="schema"/>, or null when it is valid. The schema is spliced into the
    /// installer's SQL as an identifier, so only lower-case Postgres identifiers are allowed.
    /// </summary>
    internal static string? ValidateSchema(string schema) =>
        SchemaPattern().IsMatch(schema)
            ? null
            : $"{SchemaKey} must be a lower-case Postgres identifier (letters, digits and '_', starting with a letter or '_', at most 63 characters).";

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaPattern();

    /// <summary>Startup validation of <see cref="HangfireOptions"/>. Never includes the connection string in a message.</summary>
    internal sealed class Validator : IValidateOptions<HangfireOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, HangfireOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var failures = new List<string>();

            if (options.ConnectionString is null)
            {
                failures.Add($"{ConnectionStringKey} is required when Jobs:Provider is {HangfireRegistration.ProviderName}.");
            }

            if (ValidateSchema(options.Schema) is { } schemaFailure)
            {
                failures.Add(schemaFailure);
            }

            if (options.WorkerCountInvalid || options.WorkerCount is < 1 or > MaxWorkerCount)
            {
                failures.Add($"{WorkerCountKey} must be an integer between 1 and {MaxWorkerCount}.");
            }

            if (options.DashboardEnabledInvalid)
            {
                failures.Add($"{DashboardEnabledKey} must be 'true' or 'false'.");
            }

            if (options.QueuePollInterval <= TimeSpan.Zero || options.SchedulePollingInterval <= TimeSpan.Zero)
            {
                failures.Add($"{SectionName} polling intervals must be greater than 0.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
