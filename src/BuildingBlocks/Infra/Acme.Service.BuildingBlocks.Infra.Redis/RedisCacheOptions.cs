using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Acme.Service.BuildingBlocks.Infra.Redis;

/// <summary>
/// Settings of the Redis cache provider (section <c>Cache:Redis</c>). Validated at startup (INV-17); validation
/// messages name the key but never echo the connection string (INV-28).
/// </summary>
public sealed class RedisCacheOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Cache:Redis";

    /// <summary>Full key of <see cref="ConnectionString"/>.</summary>
    public const string ConnectionStringKey = SectionName + ":ConnectionString";

    /// <summary>Full key of <see cref="ConnectTimeout"/>.</summary>
    public const string ConnectTimeoutKey = SectionName + ":ConnectTimeout";

    /// <summary>Full key of <see cref="SyncTimeout"/>.</summary>
    public const string SyncTimeoutKey = SectionName + ":SyncTimeout";

    /// <summary>Full key of <see cref="MaxValueBytes"/>.</summary>
    public const string MaxValueBytesKey = SectionName + ":MaxValueBytes";

    /// <summary>Default connect and operation timeout (WH-28): short, so an outage costs little per request.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Default maximum serialized value size: 1 MiB.</summary>
    public const int DefaultMaxValueBytes = 1024 * 1024;

    /// <summary>Upper bound for <see cref="MaxValueBytes"/>: Redis rejects strings above 512 MiB.</summary>
    public const int MaxAllowedValueBytes = 512 * 1024 * 1024;

    private static readonly TimeSpan _maxTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// StackExchange.Redis configuration string (<c>host:port,password=...,ssl=true</c>). Required. Treat it as a
    /// secret: supply it from the environment or a secret store, never from appsettings.json.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Connect timeout. Default 500 ms.</summary>
    public TimeSpan ConnectTimeout { get; set; } = DefaultTimeout;

    /// <summary>Operation timeout, applied to both synchronous and asynchronous commands. Default 500 ms.</summary>
    public TimeSpan SyncTimeout { get; set; } = DefaultTimeout;

    /// <summary>
    /// Maximum size, in bytes, of one serialized (UTF-8 JSON) value. Larger values are rejected with an
    /// <see cref="ArgumentException"/> because big values block the single Redis thread and the connection.
    /// Default 1 MiB, at most 512 MiB.
    /// </summary>
    public int MaxValueBytes { get; set; } = DefaultMaxValueBytes;

    /// <summary>
    /// Reads the section from <paramref name="configuration"/>. A value that does not parse is set to an invalid
    /// sentinel (zero) so that <see cref="Validator"/> reports the key.
    /// </summary>
    internal static void Read(IConfiguration configuration, RedisCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        options.ConnectionString = configuration[ConnectionStringKey];
        options.ConnectTimeout = ReadTimeSpan(configuration[ConnectTimeoutKey], options.ConnectTimeout);
        options.SyncTimeout = ReadTimeSpan(configuration[SyncTimeoutKey], options.SyncTimeout);

        var maxValueBytes = configuration[MaxValueBytesKey];
        if (!string.IsNullOrWhiteSpace(maxValueBytes))
        {
            options.MaxValueBytes = int.TryParse(maxValueBytes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }
    }

    /// <summary>Builds the StackExchange.Redis configuration (AbortOnConnectFail=false, fail-fast backlog).</summary>
    internal ConfigurationOptions ToConfigurationOptions()
    {
        var configuration = ConfigurationOptions.Parse(ConnectionString ?? string.Empty);
        configuration.AbortOnConnectFail = false;
        configuration.ConnectTimeout = ToMilliseconds(ConnectTimeout);
        configuration.SyncTimeout = ToMilliseconds(SyncTimeout);
        configuration.AsyncTimeout = ToMilliseconds(SyncTimeout);

        // While disconnected, fail commands at once instead of queueing them until the timeout.
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        return configuration;
    }

    private static int ToMilliseconds(TimeSpan value) => (int)Math.Ceiling(value.TotalMilliseconds);

    private static TimeSpan ReadTimeSpan(string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : TimeSpan.Zero;
    }

    /// <summary>Startup validation of <see cref="RedisCacheOptions"/>.</summary>
    internal sealed class Validator : IValidateOptions<RedisCacheOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, RedisCacheOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var failures = new List<string>();

            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                failures.Add($"{ConnectionStringKey} is required when Cache:Provider is {RedisCacheRegistration.ProviderName}.");
            }
            else if (!IsParsable(options.ConnectionString))
            {
                // Never echo the value: it may contain a password (INV-28).
                failures.Add($"{ConnectionStringKey} is not a valid StackExchange.Redis configuration string.");
            }

            if (options.ConnectTimeout <= TimeSpan.Zero || options.ConnectTimeout > _maxTimeout)
            {
                failures.Add($"{ConnectTimeoutKey} must be a duration greater than 0 and at most {_maxTimeout:c}.");
            }

            if (options.SyncTimeout <= TimeSpan.Zero || options.SyncTimeout > _maxTimeout)
            {
                failures.Add($"{SyncTimeoutKey} must be a duration greater than 0 and at most {_maxTimeout:c}.");
            }

            if (options.MaxValueBytes is <= 0 or > MaxAllowedValueBytes)
            {
                failures.Add($"{MaxValueBytesKey} must be an integer between 1 and {MaxAllowedValueBytes}.");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }

        private static bool IsParsable(string connectionString)
        {
            try
            {
                return ConfigurationOptions.Parse(connectionString).EndPoints.Count > 0;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException or RedisException)
            {
                return false;
            }
        }
    }
}
