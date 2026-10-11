using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Modules.Auth.Options;

/// <summary>
/// <c>Auth:Mfa</c> (§0.7): the issuer name authenticator apps display next to the account (the label prefix and the
/// <c>issuer</c> parameter of the <c>otpauth://</c> URI). Required and validated at startup (INV-17).
/// </summary>
internal sealed class AuthMfaOptions
{
    public const string SectionName = "Auth:Mfa";

    /// <summary>Longest accepted <see cref="Issuer"/>.</summary>
    public const int IssuerMaxLength = 64;

    /// <summary>The issuer shown by authenticator apps, for example the product name.</summary>
    public string? Issuer { get; set; }

    /// <summary>Registers the options with key-named validation and <c>ValidateOnStart</c>.</summary>
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthMfaOptions>()
            .Bind(configuration.GetSection(SectionName))
            .Validate(
                o => IsValidIssuer(o.Issuer),
                $"{SectionName}:{nameof(Issuer)} is required: 1 to {IssuerMaxLength} characters, without ':' or control characters.")
            .ValidateOnStart();
    }

    // ':' separates the issuer from the account name in the otpauth label (Key URI format).
    internal static bool IsValidIssuer(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= IssuerMaxLength
        && !value.Contains(':', StringComparison.Ordinal)
        && !value.Any(char.IsControl);
}
