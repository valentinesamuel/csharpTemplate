using Acme.Service.BuildingBlocks.UseCases;
using Acme.Service.Modules.Auth.Features.Shared;

namespace Acme.Service.Modules.Auth.Features.MfaVerify;

/// <summary>The body of <c>POST /api/v1/auth/mfa/authenticator/verify</c>.</summary>
/// <param name="Code">A six-digit code of the new authenticator key.</param>
internal sealed record MfaVerifyRequest(string? Code)
{
    // Never print the code (INV-28).
    public override string ToString() => nameof(MfaVerifyRequest);
}

/// <summary>Enables two-factor authentication once the caller proves the authenticator works.</summary>
/// <param name="UserId">The caller (<c>sub</c>).</param>
/// <param name="Code">The authenticator code.</param>
internal sealed record MfaVerifyCommand(Guid UserId, string? Code) : ICommand<RecoveryCodesResponse>
{
    // Never print the code (INV-28).
    public override string ToString() => nameof(MfaVerifyCommand);
}
