using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.RateLimiting;

/// <summary>
/// <c>Auth:RateLimiting</c> (§0.7, D-12): sliding-window limits of the anonymous account endpoints. The limiter is
/// in-process, so the effective limit is the configured limit times the number of replicas; Identity lockout (in the
/// database) is the global brute-force bound.
/// </summary>
internal sealed class AuthRateLimitingOptions
{
    public const string SectionName = "Auth:RateLimiting";

    /// <summary>Segments of every sliding window (not configurable; a permit returns at the next segment boundary).</summary>
    public const int SegmentsPerWindow = 6;

    /// <summary>Largest accepted <see cref="WindowSettings.PermitLimit"/>.</summary>
    public const int MaximumPermitLimit = 100_000;

    /// <summary>Smallest accepted <see cref="WindowSettings.Window"/>.</summary>
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(1);

    /// <summary>Largest accepted <see cref="WindowSettings.Window"/>.</summary>
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(1);

    /// <summary><c>auth-credentials</c>: login and refresh, per client IP.</summary>
    public WindowSettings Credentials { get; set; } = new() { PermitLimit = 10 };

    /// <summary><c>auth-email</c>: register and resend-confirmation, per client IP and per normalized email.</summary>
    public WindowSettings Email { get; set; } = new() { PermitLimit = 5 };

    internal sealed class WindowSettings
    {
        /// <summary>Requests allowed per <see cref="Window"/> and partition.</summary>
        public int PermitLimit { get; set; } = 10;

        /// <summary>Length of the sliding window.</summary>
        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>The limiter options: no queueing, rejected requests fail immediately.</summary>
        public SlidingWindowRateLimiterOptions ToLimiterOptions() => new()
        {
            PermitLimit = PermitLimit,
            Window = Window,
            SegmentsPerWindow = SegmentsPerWindow,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        };

        /// <summary>The longest wait until a permit returns: one segment.</summary>
        public TimeSpan SegmentLength => Window / SegmentsPerWindow;
    }

    /// <summary>Registers the options with key-named validation and <c>ValidateOnStart</c> (INV-17).</summary>
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthRateLimitingOptions>()
            .Bind(configuration.GetSection(SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthRateLimitingOptions>, AuthRateLimitingOptionsValidator>();
    }
}

/// <summary>Validates <see cref="AuthRateLimitingOptions"/>; every message names the full configuration key.</summary>
internal sealed class AuthRateLimitingOptionsValidator : IValidateOptions<AuthRateLimitingOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthRateLimitingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        Check(failures, nameof(AuthRateLimitingOptions.Credentials), options.Credentials);
        Check(failures, nameof(AuthRateLimitingOptions.Email), options.Email);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(List<string> failures, string policy, AuthRateLimitingOptions.WindowSettings? settings)
    {
        var prefix = $"{AuthRateLimitingOptions.SectionName}:{policy}";
        if (settings is null)
        {
            failures.Add($"{prefix} is required.");
            return;
        }

        if (settings.PermitLimit is < 1 or > AuthRateLimitingOptions.MaximumPermitLimit)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix}:PermitLimit must be between 1 and {AuthRateLimitingOptions.MaximumPermitLimit}."));
        }

        if (settings.Window < AuthRateLimitingOptions.MinimumWindow || settings.Window > AuthRateLimitingOptions.MaximumWindow)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix}:Window must be between {AuthRateLimitingOptions.MinimumWindow:c} and {AuthRateLimitingOptions.MaximumWindow:c}."));
        }
    }
}
