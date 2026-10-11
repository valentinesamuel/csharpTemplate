namespace Acme.Service.Modules.Auth.Features.MfaRecoveryCodes;

/// <summary>
/// Replaces the caller's recovery codes. Deliberately neither <c>ICommand</c> nor <c>IQuery</c>: a wrong password must
/// still count towards lockout, and the pipeline's transaction behaviour rolls a failed command back. The codes are
/// replaced in a single update.
/// </summary>
/// <param name="UserId">The caller (<c>sub</c>).</param>
/// <param name="Password">The current password.</param>
internal sealed record MfaRecoveryCodesCommand(Guid UserId, string Password)
{
    // Never print the password (INV-28).
    public override string ToString() => nameof(MfaRecoveryCodesCommand);
}
