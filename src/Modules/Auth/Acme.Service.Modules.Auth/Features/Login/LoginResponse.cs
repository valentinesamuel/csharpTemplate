using Acme.Service.Modules.Auth.Features.Shared;
using Acme.Service.Modules.Auth.Mfa;

namespace Acme.Service.Modules.Auth.Features.Login;

/// <summary>
/// The 200 body of <c>POST /api/v1/auth/login</c> when the account has two-factor authentication: no tokens yet, only
/// the mfa token to send with a code to <c>POST /api/v1/auth/login/mfa</c> within five minutes.
/// </summary>
/// <param name="MfaRequired">Always true.</param>
/// <param name="MfaToken">The mfa token (<c>typ=mfa+jwt</c>); never accepted as a bearer token (INV-14).</param>
internal sealed record MfaRequiredResponse(bool MfaRequired, string MfaToken)
{
    public static MfaRequiredResponse From(MfaToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return new MfaRequiredResponse(true, token.Token);
    }

    // Never print the token (INV-28).
    public override string ToString() => nameof(MfaRequiredResponse);
}

/// <summary>The outcome of a correct password: either the tokens, or the second step of a two-factor sign-in.</summary>
/// <param name="Tokens">The tokens, when the sign-in is complete.</param>
/// <param name="Mfa">The mfa challenge, when two-factor authentication is enabled.</param>
internal sealed record LoginResponse(TokenResponse? Tokens, MfaRequiredResponse? Mfa)
{
    public static LoginResponse Complete(TokenResponse tokens) => new(tokens, null);

    public static LoginResponse MfaRequired(MfaRequiredResponse mfa) => new(null, mfa);

    // Never print token values (INV-28).
    public override string ToString() => nameof(LoginResponse);
}
