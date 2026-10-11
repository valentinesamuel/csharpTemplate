namespace Acme.Service.Modules.Auth.Features.Shared;

/// <summary>
/// New recovery codes, shown once: only their SHA-256 hashes are stored (D-15), so they can never be shown again.
/// </summary>
/// <param name="RecoveryCodes">The codes; each signs in once in place of an authenticator code.</param>
internal sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes)
{
    /// <summary>Codes generated each time.</summary>
    public const int Count = 10;

    // Never print the codes (INV-28).
    public override string ToString() => nameof(RecoveryCodesResponse);
}

/// <summary>The body of the MFA endpoints that require the current password.</summary>
/// <param name="Password">The current password.</param>
internal sealed record PasswordConfirmationRequest(string Password)
{
    // Never print the password (INV-28).
    public override string ToString() => nameof(PasswordConfirmationRequest);
}
