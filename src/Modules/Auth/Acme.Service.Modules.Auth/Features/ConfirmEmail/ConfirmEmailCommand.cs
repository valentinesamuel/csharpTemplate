using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;

namespace Acme.Service.Modules.Auth.Features.ConfirmEmail;

/// <summary><c>POST /api/v1/auth/confirm-email</c>: the two values of the emailed link (body bound as is).</summary>
/// <param name="UserId">The <c>userId</c> query parameter of the link.</param>
/// <param name="Token">The <c>token</c> query parameter of the link (URL-decoded).</param>
internal sealed record ConfirmEmailCommand(Guid UserId, string Token) : ICommand<Done>
{
    // Never print the token (INV-28).
    public override string ToString() => nameof(ConfirmEmailCommand);
}
