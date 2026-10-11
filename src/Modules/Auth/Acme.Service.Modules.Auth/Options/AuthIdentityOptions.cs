using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Options;

/// <summary>
/// Binds <c>Auth:Identity</c> (§0.7) and applies it to ASP.NET Core Identity. The fixed parts of the policy are not
/// configurable: no password composition rules (NIST 800-63B), unique emails, lockout enabled for new users.
/// </summary>
internal sealed class AuthIdentityOptions
{
    public const string SectionName = "Auth:Identity";

    /// <summary>Lowest accepted <see cref="PasswordSettings.RequiredLength"/>; the policy floor (plan P11).</summary>
    public const int MinimumPasswordLength = 12;

    /// <summary>Highest accepted <see cref="PasswordSettings.RequiredLength"/>.</summary>
    public const int MaximumPasswordLength = 128;

    /// <summary>
    /// Whether sign-in requires a confirmed email. Enforced by the login use case (P13) after the password check
    /// (D-23), so it is deliberately not mapped to <c>IdentityOptions.SignIn.RequireConfirmedEmail</c>, which
    /// would reject before the password is verified.
    /// </summary>
    public bool RequireConfirmedEmail { get; set; } = true;

    public PasswordSettings Password { get; set; } = new();

    public LockoutSettings Lockout { get; set; } = new();

    /// <summary>Lifetime of email confirmation and password reset tokens.</summary>
    public TimeSpan TokenLifespan { get; set; } = TimeSpan.FromHours(1);

    internal sealed class PasswordSettings
    {
        public int RequiredLength { get; set; } = MinimumPasswordLength;
    }

    internal sealed class LockoutSettings
    {
        public int MaxFailedAccessAttempts { get; set; } = 5;

        public TimeSpan DefaultLockoutTimeSpan { get; set; } = TimeSpan.FromMinutes(15);
    }

    /// <summary>Registers the options with key-named validation and <c>ValidateOnStart</c> (INV-17), and maps them onto Identity.</summary>
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthIdentityOptions>()
            .Bind(configuration.GetSection(SectionName))
            .Validate(
                o => o.Password.RequiredLength is >= MinimumPasswordLength and <= MaximumPasswordLength,
                $"{SectionName}:Password:RequiredLength must be between {MinimumPasswordLength} and {MaximumPasswordLength}.")
            .Validate(
                o => o.Lockout.MaxFailedAccessAttempts >= 1,
                $"{SectionName}:Lockout:MaxFailedAccessAttempts must be at least 1.")
            .Validate(
                o => o.Lockout.DefaultLockoutTimeSpan > TimeSpan.Zero,
                $"{SectionName}:Lockout:DefaultLockoutTimeSpan must be positive.")
            .Validate(
                o => o.TokenLifespan > TimeSpan.Zero,
                $"{SectionName}:TokenLifespan must be positive.")
            .ValidateOnStart();

        services.AddOptions<IdentityOptions>()
            .Configure<IOptions<AuthIdentityOptions>>((identity, auth) => Apply(auth.Value, identity));

        services.AddOptions<DataProtectionTokenProviderOptions>()
            .Configure<IOptions<AuthIdentityOptions>>((tokens, auth) => tokens.TokenLifespan = auth.Value.TokenLifespan);
    }

    /// <summary>Copies the policy onto <paramref name="identity"/>.</summary>
    internal static void Apply(AuthIdentityOptions options, IdentityOptions identity)
    {
        identity.Password.RequiredLength = options.Password.RequiredLength;
        identity.Password.RequireDigit = false;
        identity.Password.RequireUppercase = false;
        identity.Password.RequireLowercase = false;
        identity.Password.RequireNonAlphanumeric = false;
        identity.Password.RequiredUniqueChars = 1;

        identity.User.RequireUniqueEmail = true;

        identity.Lockout.AllowedForNewUsers = true;
        identity.Lockout.MaxFailedAccessAttempts = options.Lockout.MaxFailedAccessAttempts;
        identity.Lockout.DefaultLockoutTimeSpan = options.Lockout.DefaultLockoutTimeSpan;
    }
}
