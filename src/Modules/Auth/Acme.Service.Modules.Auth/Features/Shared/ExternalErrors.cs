using Acme.Service.BuildingBlocks.Results;

namespace Acme.Service.Modules.Auth.Features.Shared;

/// <summary>Client-visible failures of the external login endpoints (D-13, D-14).</summary>
internal static class ExternalErrors
{
    /// <summary>The return URL is not exactly one of <c>Auth:External:Oidc:AllowedReturnUrls</c> (400): no open redirect.</summary>
    public static readonly Error InvalidReturnUrl = Error.Validation(
        "invalid_return_url",
        "The return URL is not allowed.",
        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["returnUrl"] = ["The return URL is not allowed."] });

    /// <summary>The external sign-in could not be completed (no or expired external cookie, no usable email, a locked account).</summary>
    public static readonly Error LoginFailed = Error.Validation("external_login_failed", "The external sign-in failed.");

    /// <summary>The provider could not be reached (503); local sign-in is unaffected.</summary>
    public static readonly Error ProviderUnavailable = Error.Unavailable(
        "external_provider_unavailable", "The external sign-in provider is unavailable. Try again later.");

    /// <summary>The one-time code is malformed, unknown, used or expired (401). Never says which.</summary>
    public static readonly Error InvalidExchangeCode = Error.Unauthorized(
        "invalid_exchange_code", "The sign-in code is invalid or has expired. Sign in again.");
}
