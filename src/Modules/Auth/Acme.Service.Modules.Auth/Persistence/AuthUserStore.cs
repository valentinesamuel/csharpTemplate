using System.Security.Cryptography;
using System.Text;
using Acme.Service.Modules.Auth.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Acme.Service.Modules.Auth.Persistence;

/// <summary>
/// The Identity user store with the MFA factors protected at rest (D-15, INV-28, WH-10):
/// <list type="bullet">
/// <item>the authenticator (TOTP) key is encrypted with Data Protection, purpose <see cref="AuthenticatorKeyPurpose"/>;</item>
/// <item>recovery codes are stored only as SHA-256 hashes; a code redeems once.</item>
/// </list>
/// Both live in <c>auth.user_tokens</c> under Identity's internal token names.
/// </summary>
internal sealed class AuthUserStore(
    AuthDbContext context,
    IDataProtectionProvider dataProtectionProvider,
    IdentityErrorDescriber? describer = null)
    : UserStore<AuthUser, AuthRole, AuthDbContext, Guid>(context, describer)
{
    /// <summary>Data Protection purpose of the authenticator key.</summary>
    public const string AuthenticatorKeyPurpose = "auth.totp";

    // Identity's internal token names (UserStoreBase): the store keeps the MFA factors under them in auth.user_tokens.
    private const string InternalLoginProvider = "[AspNetUserStore]";
    private const string RecoveryCodeTokenName = "RecoveryCodes";

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(AuthenticatorKeyPurpose);

    public override Task SetAuthenticatorKeyAsync(AuthUser user, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return base.SetAuthenticatorKeyAsync(user, _protector.Protect(key), cancellationToken);
    }

    public override async Task<string?> GetAuthenticatorKeyAsync(AuthUser user, CancellationToken cancellationToken)
    {
        var stored = await base.GetAuthenticatorKeyAsync(user, cancellationToken).ConfigureAwait(false);
        return stored is null ? null : _protector.Unprotect(stored);
    }

    public override Task ReplaceCodesAsync(AuthUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodes);
        return base.ReplaceCodesAsync(user, [.. recoveryCodes.Select(HashRecoveryCode)], cancellationToken);
    }

    /// <remarks>
    /// Re-implements the base method rather than delegating to it: the base stores the remaining codes through the
    /// virtual <see cref="ReplaceCodesAsync"/>, which would hash the already hashed codes again and break every code left.
    /// </remarks>
    public override async Task<bool> RedeemCodeAsync(AuthUser user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(code);

        var hashed = HashRecoveryCode(code);
        var stored = await GetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        var codes = stored.Split(';');
        if (!codes.Contains(hashed, StringComparer.Ordinal))
        {
            return false;
        }

        await base.ReplaceCodesAsync(user, [.. codes.Where(c => !string.Equals(c, hashed, StringComparison.Ordinal))], cancellationToken).ConfigureAwait(false);
        return true;
    }

    // CountCodesAsync is inherited unchanged: it counts the stored entries, which are the hashes.

    /// <summary>Lower-case hex SHA-256 of the UTF-8 code. Hex contains no ';', Identity's code separator.</summary>
    internal static string HashRecoveryCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
