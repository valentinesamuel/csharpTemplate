using Acme.Service.BuildingBlocks.UseCases;
using Microsoft.AspNetCore.Identity;

namespace Acme.Service.Modules.Auth.Features.ExternalCallback;

/// <summary>
/// Completes an external login from the provider's identity (read from the external cookie by
/// <see cref="SignInManager{TUser}.GetExternalLoginInfoAsync"/>): finds or provisions the local user and issues a
/// one-time code. A command: provisioning (user, login, default role, event) and the code commit together.
/// </summary>
/// <param name="Login">The provider, the provider's user key and the provider's claims.</param>
internal sealed record ExternalCallbackCommand(ExternalLoginInfo Login) : ICommand<ExternalCallbackResult>
{
    // Never print the provider's claims (personal data).
    public override string ToString() => nameof(ExternalCallbackCommand);
}

/// <summary>How an external login ended.</summary>
internal enum ExternalCallbackOutcome
{
    /// <summary>A user already linked to the provider's key signed in.</summary>
    SignedIn,

    /// <summary>A new local user was created and linked (no local account had the email).</summary>
    Provisioned,

    /// <summary>A local account already uses the email but is not linked: nothing changed, no code (D-14).</summary>
    AccountExists,
}

/// <summary>The outcome; <see cref="Code"/> is set for <see cref="ExternalCallbackOutcome.SignedIn"/> and <see cref="ExternalCallbackOutcome.Provisioned"/>.</summary>
/// <param name="Outcome">How the login ended.</param>
/// <param name="UserId">The local user, when signed in or provisioned.</param>
/// <param name="Code">The one-time code for <c>POST /external/exchange</c>.</param>
internal sealed record ExternalCallbackResult(ExternalCallbackOutcome Outcome, Guid? UserId, string? Code)
{
    public static readonly ExternalCallbackResult AccountExists = new(ExternalCallbackOutcome.AccountExists, null, null);

    // Never print the code (INV-28).
    public override string ToString() => $"{nameof(ExternalCallbackResult)} {{ Outcome = {Outcome} }}";
}
