using Acme.Service.Modules.Auth.Options;
using Acme.Service.Modules.Auth.Persistence.Configurations;
using FluentValidation;

namespace Acme.Service.Modules.Auth.Features.Shared;

/// <summary>Validation rules shared by the account commands (D-18). Messages never echo passwords or tokens.</summary>
internal static class AccountValidation
{
    /// <summary>Longest accepted email address (the column length of <c>auth.users.email</c>).</summary>
    public const int EmailMaxLength = IdentityModelLimits.Default;

    /// <summary>Longest accepted one-time token (Identity's data protection tokens are a few hundred characters).</summary>
    public const int TokenMaxLength = 2048;

    /// <summary>Longest accepted recovery code (Identity's codes are 11 characters, <c>XXXXX-XXXXX</c>).</summary>
    public const int RecoveryCodeMaxLength = 32;

    /// <summary>A six-digit authenticator code.</summary>
    public static IRuleBuilderOptions<T, string?> AuthenticatorCode<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().Matches("^[0-9]{6}$").WithMessage("The code must be 6 digits.");

    /// <summary>A present email address of plausible shape and length.</summary>
    public static IRuleBuilderOptions<T, string> ValidEmail<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(EmailMaxLength).EmailAddress();

    /// <summary>A present password no longer than the policy maximum (sign-in: the length policy is not re-checked).</summary>
    public static IRuleBuilderOptions<T, string> PresentPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(AuthIdentityOptions.MaximumPasswordLength);
}
