using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.External;

/// <summary>
/// <c>Auth:External:Oidc</c> (§0.7, D-13): the optional generic OpenID Connect login, off by default. When
/// <see cref="Enabled"/> is true every other setting is validated at startup (INV-17); when false nothing is checked and
/// neither the handler nor the endpoints exist.
/// </summary>
internal sealed class ExternalOidcOptions
{
    public const string SectionName = "Auth:External:Oidc";

    /// <summary>The authentication scheme (and Identity login provider) name of the external login.</summary>
    public const string Scheme = "oidc";

    /// <summary>The scope every OpenID Connect request must contain.</summary>
    public const string OpenIdScope = "openid";

    /// <summary>Whether the external login is on. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>The provider name shown to users, for example the company's identity provider.</summary>
    public string DisplayName { get; set; } = "OpenID Connect";

    /// <summary>The issuer URL; the discovery document is read from <c>{Authority}/.well-known/openid-configuration</c>.</summary>
    public string? Authority { get; set; }

    /// <summary>The client id registered at the provider.</summary>
    public string? ClientId { get; set; }

    /// <summary>The client secret registered at the provider. A secret: never logged.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>The requested scopes; must include <c>openid</c>. Default <c>openid profile email</c>.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    /// The client pages the callback may redirect to, compared to <c>returnUrl</c> by exact, case-sensitive string
    /// equality (no prefix or host matching). Absolute http(s) URLs without a query or fragment. Required when enabled.
    /// </summary>
    public string[] AllowedReturnUrls { get; set; } = [];

    /// <summary>The scopes requested from the provider: <see cref="Scopes"/>, or <c>openid profile email</c> when none are configured.</summary>
    public IReadOnlyList<string> EffectiveScopes => Scopes.Length > 0 ? Scopes : [OpenIdScope, "profile", "email"];

    /// <summary>True when <paramref name="returnUrl"/> is exactly one of <see cref="AllowedReturnUrls"/>.</summary>
    public bool IsAllowedReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && AllowedReturnUrls.Contains(returnUrl, StringComparer.Ordinal);

    /// <summary>Registers the options with startup validation. <paramref name="allowHttpAuthority"/> admits an http authority (Development).</summary>
    public static void Register(IServiceCollection services, IConfiguration configuration, bool allowHttpAuthority)
    {
        services.AddOptions<ExternalOidcOptions>()
            .Bind(configuration.GetSection(SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ExternalOidcOptions>>(new Validator(allowHttpAuthority));
    }

    /// <summary>The first configuration problem, naming the key, or null when the options are valid (or disabled).</summary>
    internal static string? Validate(ExternalOidcOptions options, bool allowHttpAuthority)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return null;
        }

        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority)
            || !(authority.Scheme == Uri.UriSchemeHttps || (allowHttpAuthority && authority.Scheme == Uri.UriSchemeHttp)))
        {
            return $"{SectionName}:{nameof(Authority)} is required when {SectionName}:{nameof(Enabled)}=true: an absolute https URL"
                + (allowHttpAuthority ? " (http is allowed in Development)." : ".");
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            return $"{SectionName}:{nameof(ClientId)} is required when {SectionName}:{nameof(Enabled)}=true.";
        }

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return $"{SectionName}:{nameof(ClientSecret)} is required when {SectionName}:{nameof(Enabled)}=true.";
        }

        if (string.IsNullOrWhiteSpace(options.DisplayName))
        {
            return $"{SectionName}:{nameof(DisplayName)} must not be empty.";
        }

        if (options.Scopes.Length > 0
            && (!options.Scopes.Contains(OpenIdScope, StringComparer.Ordinal) || options.Scopes.Any(s => string.IsNullOrWhiteSpace(s) || s.Any(char.IsWhiteSpace))))
        {
            return $"{SectionName}:{nameof(Scopes)} must contain '{OpenIdScope}' and only non-empty scope names without spaces.";
        }

        if (options.AllowedReturnUrls.Length == 0)
        {
            return $"{SectionName}:{nameof(AllowedReturnUrls)} must list at least one URL when {SectionName}:{nameof(Enabled)}=true.";
        }

        foreach (var url in options.AllowedReturnUrls)
        {
            if (!IsValidReturnUrl(url))
            {
                return $"{SectionName}:{nameof(AllowedReturnUrls)} entries must be absolute http or https URLs without a query or fragment.";
            }
        }

        return null;
    }

    // The callback appends ?code= or ?error=, so an entry carries no query of its own.
    private static bool IsValidReturnUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && !url.Contains('?', StringComparison.Ordinal)
        && !url.Contains('#', StringComparison.Ordinal);

    private sealed class Validator(bool allowHttpAuthority) : IValidateOptions<ExternalOidcOptions>
    {
        public ValidateOptionsResult Validate(string? name, ExternalOidcOptions options)
        {
            var failure = ExternalOidcOptions.Validate(options, allowHttpAuthority);
            return failure is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failure);
        }
    }
}
