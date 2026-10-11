using Acme.Service.BuildingBlocks.Results;
using Acme.Service.BuildingBlocks.UseCases.Behaviors;
using Microsoft.AspNetCore.Identity;

namespace Acme.Service.Modules.Auth.Features.Shared;

/// <summary>
/// Client-visible failures of the account endpoints. The messages are deliberately generic so that a response never
/// tells whether an account exists or why a sign-in failed (D-23).
/// </summary>
internal static class AccountErrors
{
    /// <summary>Unknown email, wrong password or locked account (401): never distinguished (D-23).</summary>
    public static readonly Error InvalidCredentials = Error.Unauthorized(
        "invalid_credentials", "The email or password is incorrect.");

    /// <summary>The password was correct but the email address is not confirmed yet (403, D-23).</summary>
    public static readonly Error EmailNotVerified = Error.Forbidden(
        "email_not_verified", "The email address has not been confirmed. Check your inbox or request a new confirmation email.");

    /// <summary>An unknown user, a malformed, expired or already invalidated confirmation token (400).</summary>
    public static readonly Error InvalidToken = Error.Validation(
        "invalid_token", "The link is invalid or has expired.");

    /// <summary>The mfa token is malformed or expired, or its challenge was consumed, exhausted or never existed (401).</summary>
    public static readonly Error InvalidMfaToken = Error.Unauthorized(
        "invalid_mfa_token", "The sign-in attempt is invalid or has expired. Sign in again.");

    /// <summary>A wrong, reused or expired authenticator code or recovery code at sign-in, or a locked account (401).</summary>
    public static readonly Error InvalidMfaCode = Error.Unauthorized(
        "invalid_mfa_code", "The code is incorrect.");

    /// <summary>Two-factor authentication is on; the authenticator cannot be replaced before it is disabled (409).</summary>
    public static readonly Error MfaAlreadyEnabled = Error.Conflict(
        "mfa_already_enabled", "Two-factor authentication is already enabled. Disable it before setting up a new authenticator.");

    /// <summary>The operation needs two-factor authentication to be on (409).</summary>
    public static readonly Error MfaNotEnabled = Error.Conflict(
        "mfa_not_enabled", "Two-factor authentication is not enabled.");

    /// <summary>Concurrent changes to the same account (409).</summary>
    public static readonly Error ConcurrencyConflict = Error.Conflict(
        "concurrency_conflict", "The account was changed at the same time. Try again.");

    /// <summary>
    /// The current password given to confirm a sensitive change is wrong, or the account is locked (400). Counted
    /// towards lockout.
    /// </summary>
    public static Error IncorrectPassword(string field) => Error.Validation(
        "invalid_password",
        "The password is incorrect.",
        new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = ["The password is incorrect."] });

    /// <summary>The authenticator code that should enable two-factor authentication is wrong or expired (400).</summary>
    public static readonly Error InvalidVerificationCode = Error.Validation(
        "invalid_code",
        "The code is incorrect or has expired.",
        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["code"] = ["The code is incorrect or has expired."] });

    /// <summary>
    /// The 400 of a new password rejected by Identity's password policy: the descriptions, which never contain the
    /// password, under <paramref name="field"/>.
    /// </summary>
    public static Error PasswordRejected(IdentityResult result, string field)
    {
        ArgumentNullException.ThrowIfNull(result);

        var messages = result.Errors.Select(e => e.Description).Distinct(StringComparer.Ordinal).ToArray();
        return Error.Validation(
            ValidationBehavior<object, Done>.ErrorCode,
            "One or more validation errors occurred.",
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = messages });
    }
}
