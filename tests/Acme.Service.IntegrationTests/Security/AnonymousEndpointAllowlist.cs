namespace Acme.Service.IntegrationTests.Security;

/// <summary>
/// The only endpoints allowed to be anonymous (INV-12). Every other endpoint requires an authenticated caller. Adding an
/// entry is a security decision: the endpoint must also call <c>.AllowAnonymous()</c> explicitly, and the reason is
/// recorded next to the entry. Keys are route patterns as written (<c>RoutePattern.RawText</c>).
/// </summary>
internal static class AnonymousEndpointAllowlist
{
    public static readonly IReadOnlyDictionary<string, string> Patterns = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/health/live"] = "Liveness probe; process only, no data (D-20).",
        ["/health/ready"] = "Readiness probe; dependency status only, no data (D-20).",
        ["/.well-known/jwks.json"] = "Public signing keys; clients and resource servers validate tokens with them (D-08).",
        ["/api/v1/auth/register"] = "Self-registration; always 202 with a generic body, rate limited per IP and email (D-23, D-12).",
        ["/api/v1/auth/confirm-email"] = "Email confirmation from an emailed link; needs the one-time token (D-23).",
        ["/api/v1/auth/resend-confirmation"] = "New confirmation email; always 202, rate limited per IP and email (D-23, D-12).",
        ["/api/v1/auth/login"] = "Password sign-in; generic 401, Identity lockout, rate limited per IP (D-23, D-12).",
        ["/api/v1/auth/refresh"] = "Refresh token rotation; the refresh token is the credential, rate limited per IP (D-11, D-12).",
        ["/api/v1/auth/login/mfa"] = "Second sign-in step; needs the 5-minute mfa token and a code, 5 attempts per challenge, Identity lockout, rate limited per IP (D-15, D-12).",
        ["/api/v1/auth/forgot-password"] = "Password reset request; always 202 with a generic body, rate limited per IP and email (D-23, D-12).",
        ["/api/v1/auth/reset-password"] = "Password reset from an emailed link; needs the one-time token, rate limited per IP (D-23, D-12).",
        ["/api/v1/auth/external/oidc/challenge"] = "Starts the external OIDC login; mapped only when Auth:External:Oidc:Enabled=true, returnUrl must exactly match the allowlist (D-13).",
        ["/api/v1/auth/external/oidc/callback"] = "Completes the external OIDC login from the 5-minute external cookie; mapped only when enabled, redirects only to an allowlisted returnUrl, never auto-links (D-13, D-14).",
        ["/api/v1/auth/external/exchange"] = "Exchanges the single-use, 60-second, hashed external login code for tokens; mapped only when enabled, rate limited per IP (D-13, D-12).",
        ["/openapi/{documentName}.json"] = "OpenAPI document; mapped only when OpenApi:Enabled=true (Development, D-24).",
        ["/scalar/{documentName?}"] = "Scalar API reference UI; mapped only when OpenApi:Enabled=true (Development, D-24).",
        ["/scalar/scalar.js"] = "Scalar UI static asset (Development, D-24).",
        ["/scalar/scalar.aspnetcore.js"] = "Scalar UI static asset (Development, D-24).",
        ["/scalar/favicon.svg"] = "Scalar UI static asset (Development, D-24).",
        ["/hangfire/{**path}"] = "Hangfire dashboard; Development only and local requests only (D-21, R-24).",
    };
}
