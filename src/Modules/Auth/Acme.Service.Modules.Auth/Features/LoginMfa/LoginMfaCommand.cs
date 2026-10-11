using Acme.Service.Modules.Auth.Sessions;

namespace Acme.Service.Modules.Auth.Features.LoginMfa;

/// <summary>The body of <c>POST /api/v1/auth/login/mfa</c>.</summary>
/// <param name="MfaToken">The mfa token returned by <c>POST /api/v1/auth/login</c>.</param>
/// <param name="Code">A six-digit authenticator code; exactly one of <paramref name="Code"/> and <paramref name="RecoveryCode"/>.</param>
/// <param name="RecoveryCode">An unused recovery code.</param>
internal sealed record LoginMfaRequest(string? MfaToken, string? Code, string? RecoveryCode)
{
    // Never print the token or the codes (INV-28).
    public override string ToString() => nameof(LoginMfaRequest);
}

/// <summary>
/// The second step of a two-factor sign-in. Deliberately neither <c>ICommand</c> nor <c>IQuery</c>, like the password
/// step: a wrong code must still persist (the challenge's attempt counter and Identity's failed-attempt counter), and
/// the pipeline's transaction behaviour rolls a failed command back. Validation and logging still run in the pipeline.
/// </summary>
/// <param name="MfaToken">The mfa token.</param>
/// <param name="Code">The authenticator code, or null.</param>
/// <param name="RecoveryCode">The recovery code, or null.</param>
/// <param name="Client">Client details recorded with the refresh token.</param>
internal sealed record LoginMfaCommand(string? MfaToken, string? Code, string? RecoveryCode, ClientInfo Client)
{
    // Never print the token or the codes (INV-28).
    public override string ToString() => nameof(LoginMfaCommand);
}
