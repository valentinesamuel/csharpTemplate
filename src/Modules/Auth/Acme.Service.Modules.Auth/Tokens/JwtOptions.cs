using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Tokens;

/// <summary>
/// <c>Auth:Jwt</c> (§0.7): issuer, audience, token and session lifetimes, clock skew and the signing key ring (D-08).
/// Validated at startup (INV-17); an invalid key ring aborts startup and never degrades to "accept" (INV-13).
/// </summary>
internal sealed class JwtOptions
{
    public const string SectionName = "Auth:Jwt";

    /// <summary>Largest accepted <see cref="ClockSkew"/>.</summary>
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>The <c>iss</c> claim of issued tokens and the only accepted issuer.</summary>
    public string? Issuer { get; set; }

    /// <summary>The <c>aud</c> claim of issued tokens and the only accepted audience.</summary>
    public string? Audience { get; set; }

    /// <summary>Access token lifetime.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Lifetime of each refresh token (capped by the session's absolute expiry).</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Absolute lifetime of a session (refresh token family), whatever the refresh activity.</summary>
    public TimeSpan SessionAbsoluteLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Tolerated clock difference when validating <c>exp</c> and <c>nbf</c>.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The signing key ring.</summary>
    public IList<SigningKeyOptions> SigningKeys { get; } = [];

    /// <summary>Registers the options with key-named validation and <c>ValidateOnStart</c>.</summary>
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
    }
}

/// <summary>
/// Validates <see cref="JwtOptions"/>, including every rule of the key ring: exactly one <c>Active</c> key, unique
/// <c>Kid</c>s, RSA of at least 2048 bits, exactly one of <c>PrivateKeyPath</c>/<c>PrivateKeyBase64</c> per key.
/// Every message names the full configuration key and never contains key material.
/// </summary>
internal sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    private const string Keys = JwtOptions.SectionName + ":SigningKeys";

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add($"{JwtOptions.SectionName}:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add($"{JwtOptions.SectionName}:Audience is required.");
        }

        RequirePositive(failures, options.AccessTokenLifetime, nameof(JwtOptions.AccessTokenLifetime));
        RequirePositive(failures, options.RefreshTokenLifetime, nameof(JwtOptions.RefreshTokenLifetime));
        RequirePositive(failures, options.SessionAbsoluteLifetime, nameof(JwtOptions.SessionAbsoluteLifetime));
        if (options.ClockSkew < TimeSpan.Zero || options.ClockSkew > JwtOptions.MaximumClockSkew)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{JwtOptions.SectionName}:ClockSkew must be between 00:00:00 and {JwtOptions.MaximumClockSkew:c}."));
        }

        ValidateKeyRing(failures, options.SigningKeys);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePositive(List<string> failures, TimeSpan value, string key)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{JwtOptions.SectionName}:{key} must be positive.");
        }
    }

    private static void ValidateKeyRing(List<string> failures, IList<SigningKeyOptions> keys)
    {
        var activeCount = 0;
        var kids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var path = string.Create(CultureInfo.InvariantCulture, $"{Keys}:{i}");

            if (!SigningKeyOptions.IsValidKid(key.Kid))
            {
                failures.Add($"{path}:Kid is required: 1 to {SigningKeyOptions.KidMaxLength} characters of A-Z, a-z, 0-9, '.', '_' or '-'.");
            }
            else if (!kids.Add(key.Kid!))
            {
                failures.Add($"{path}:Kid '{key.Kid}' is used by more than one key; Kid values must be unique.");
            }

            if (!key.TryGetStatus(out var status))
            {
                failures.Add($"{path}:Status must be one of Active, Next or Retiring.");
            }
            else if (status == SigningKeyStatus.Active)
            {
                activeCount++;
            }

            using var rsa = key.TryLoadPrivateKey(path, out var error);
            if (error is not null)
            {
                failures.Add(error);
            }
        }

        if (activeCount != 1)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Keys} must contain exactly one key with Status=Active (found {activeCount}). For local development run 'dotnet run scripts/dev-env.cs'."));
        }
    }
}
