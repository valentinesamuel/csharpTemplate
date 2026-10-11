using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Service.Modules.Auth.Options;

/// <summary>
/// <c>Auth:Links</c> (§0.7): the client pages that emailed links point to. The Auth module appends the query
/// parameters (<c>userId</c>, <c>token</c>); the page posts them back to the API. Required, so a deployment never
/// sends links to a wrong host (INV-17).
/// </summary>
internal sealed class AuthLinksOptions
{
    public const string SectionName = "Auth:Links";

    /// <summary>Absolute http(s) URL of the page that confirms an email address (it calls <c>POST /api/v1/auth/confirm-email</c>).</summary>
    public string? ConfirmEmailUrl { get; set; }

    /// <summary>Absolute http(s) URL of the page that sets a new password (it calls <c>POST /api/v1/auth/reset-password</c>).</summary>
    public string? ResetPasswordUrl { get; set; }

    /// <summary>Registers the options with key-named validation and <c>ValidateOnStart</c>.</summary>
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthLinksOptions>()
            .Bind(configuration.GetSection(SectionName))
            .Validate(
                o => IsAbsoluteHttpUrl(o.ConfirmEmailUrl),
                $"{SectionName}:{nameof(ConfirmEmailUrl)} is required: an absolute http or https URL without a fragment.")
            .Validate(
                o => IsAbsoluteHttpUrl(o.ResetPasswordUrl),
                $"{SectionName}:{nameof(ResetPasswordUrl)} is required: an absolute http or https URL without a fragment.")
            .ValidateOnStart();
    }

    internal static bool IsAbsoluteHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.Fragment);
}
