namespace Acme.Service.Modules.Auth.Features.ChangePassword;

/// <summary>The body of <c>POST /api/v1/auth/change-password</c>.</summary>
/// <param name="CurrentPassword">The current password.</param>
/// <param name="NewPassword">The new password (the length policy of <c>Auth:Identity:Password</c>).</param>
internal sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword)
{
    // Never print the passwords (INV-28).
    public override string ToString() => nameof(ChangePasswordRequest);
}

/// <summary>
/// Changes the caller's password. Deliberately neither <c>ICommand</c> nor <c>IQuery</c>: a wrong current password
/// must still count towards lockout (Identity writes the counter), and the pipeline's transaction behaviour rolls a
/// failed command back. The change itself runs in one Auth unit of work. Validation and logging still run in the
/// pipeline.
/// </summary>
/// <param name="UserId">The caller (<c>sub</c>).</param>
/// <param name="SessionId">The caller's session (<c>sid</c>), the only one kept.</param>
/// <param name="CurrentPassword">The current password.</param>
/// <param name="NewPassword">The new password.</param>
internal sealed record ChangePasswordCommand(Guid UserId, Guid SessionId, string CurrentPassword, string NewPassword)
{
    // Never print the passwords (INV-28).
    public override string ToString() => nameof(ChangePasswordCommand);
}
