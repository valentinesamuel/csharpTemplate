using Acme.Service.BuildingBlocks.UseCases;

namespace Acme.Service.Modules.Auth.Features.MfaSetup;

/// <summary>Gives the caller a new authenticator key (two-factor authentication stays off until verified).</summary>
/// <param name="UserId">The caller (<c>sub</c>).</param>
internal sealed record MfaSetupCommand(Guid UserId) : ICommand<MfaSetupResponse>;

/// <summary>The new authenticator key, to enter in an authenticator app, or the same as an <c>otpauth://</c> URI.</summary>
/// <param name="SharedKey">The Base32 key.</param>
/// <param name="AuthenticatorUri"><c>otpauth://totp/{Issuer}:{email}?secret=…&amp;issuer=…&amp;digits=6&amp;period=30</c>.</param>
internal sealed record MfaSetupResponse(string SharedKey, string AuthenticatorUri)
{
    // Never print the key (INV-28).
    public override string ToString() => nameof(MfaSetupResponse);
}
