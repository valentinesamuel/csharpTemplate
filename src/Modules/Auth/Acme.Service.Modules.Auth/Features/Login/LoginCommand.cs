using Acme.Service.Modules.Auth.Sessions;

namespace Acme.Service.Modules.Auth.Features.Login;

/// <summary>The body of <c>POST /api/v1/auth/login</c>.</summary>
/// <param name="Email">The account's email address.</param>
/// <param name="Password">The password.</param>
internal sealed record LoginRequest(string Email, string Password)
{
    // Never print the password (INV-28).
    public override string ToString() => nameof(LoginRequest);
}

/// <summary>
/// Password sign-in. Deliberately neither <c>ICommand</c> nor <c>IQuery</c>: the use case commits its own writes,
/// because a failed sign-in must still persist (the failed-attempt counter and lockout, written by Identity), and the
/// pipeline's transaction behaviour rolls a failed command back. The session is created in its own Auth unit of work
/// by <see cref="RefreshTokenService"/>. Validation and logging still run in the pipeline.
/// </summary>
/// <param name="Email">The account's email address.</param>
/// <param name="Password">The password.</param>
/// <param name="Client">Client details recorded with the refresh token.</param>
internal sealed record LoginCommand(string Email, string Password, ClientInfo Client)
{
    // Never print the password (INV-28).
    public override string ToString() => nameof(LoginCommand);
}
