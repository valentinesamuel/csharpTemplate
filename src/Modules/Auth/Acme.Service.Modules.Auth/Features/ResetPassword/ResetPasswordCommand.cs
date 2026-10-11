using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;

namespace Acme.Service.Modules.Auth.Features.ResetPassword;

/// <summary><c>POST /api/v1/auth/reset-password</c>: the two values of the emailed link and the new password (body bound as is).</summary>
/// <param name="UserId">The <c>userId</c> query parameter of the link.</param>
/// <param name="Token">The <c>token</c> query parameter of the link (URL-decoded).</param>
/// <param name="NewPassword">The new password (the length policy of <c>Auth:Identity:Password</c>).</param>
internal sealed record ResetPasswordCommand(Guid UserId, string Token, string NewPassword) : ICommand<Done>
{
    // Never print the token or the password (INV-28).
    public override string ToString() => nameof(ResetPasswordCommand);
}
