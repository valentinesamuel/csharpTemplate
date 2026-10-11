namespace Acme.Service.Modules.Auth.Features.MfaDisable;

/// <summary>
/// Turns two-factor authentication off. Deliberately neither <c>ICommand</c> nor <c>IQuery</c>: a wrong password must
/// still count towards lockout (Identity writes the counter), and the pipeline's transaction behaviour rolls a failed
/// command back. The change itself runs in one Auth unit of work.
/// </summary>
/// <param name="UserId">The caller (<c>sub</c>).</param>
/// <param name="Password">The current password.</param>
internal sealed record MfaDisableCommand(Guid UserId, string Password)
{
    // Never print the password (INV-28).
    public override string ToString() => nameof(MfaDisableCommand);
}
