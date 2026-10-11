using Acme.Service.Modules.Auth.Sessions;

namespace Acme.Service.Modules.Auth.Features.Refresh;

/// <summary>The body of <c>POST /api/v1/auth/refresh</c> and <c>POST /api/v1/auth/logout</c>.</summary>
/// <param name="RefreshToken">The refresh token.</param>
internal sealed record RefreshTokenRequest(string? RefreshToken)
{
    // Never print the token (INV-28).
    public override string ToString() => nameof(RefreshTokenRequest);
}

/// <summary>
/// Refresh token rotation. Deliberately neither <c>ICommand</c> nor <c>IQuery</c>: <see cref="RefreshTokenService"/>
/// runs its own Auth unit of work, which must commit the revocation of a reused token's session even though the
/// exchange fails (D-11); the pipeline's transaction behaviour would roll a failed command back.
/// </summary>
/// <param name="RefreshToken">The refresh token presented by the client.</param>
/// <param name="Client">Client details recorded with the new refresh token.</param>
internal sealed record RefreshCommand(string? RefreshToken, ClientInfo Client)
{
    // Never print the token (INV-28).
    public override string ToString() => nameof(RefreshCommand);
}
