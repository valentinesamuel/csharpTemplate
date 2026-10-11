using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;

namespace Acme.Service.Modules.Auth.Features.Logout;

/// <summary>Ends the session that <see cref="RefreshToken"/> belongs to, if it is the caller's.</summary>
/// <param name="UserId">The caller (<c>sub</c>).</param>
/// <param name="RefreshToken">A refresh token of the session to end (used or not).</param>
internal sealed record LogoutCommand(Guid UserId, string? RefreshToken) : ICommand<Done>
{
    // Never print the token (INV-28).
    public override string ToString() => nameof(LogoutCommand);
}
