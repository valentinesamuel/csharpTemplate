using System.Security.Cryptography;
using Acme.Service.Modules.Auth.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Acme.Service.Modules.Auth.Features.Shared;

/// <summary>
/// Spends the same work as a real password verification on paths that would otherwise skip it (an unknown email at
/// sign-in, a locked account, an existing email at registration), so response times do not reveal whether an account
/// exists (D-23). Singleton: the dummy hash is created once, from a random password nobody knows, with Identity's
/// <see cref="PasswordHasher{TUser}"/> and the configured <see cref="PasswordHasherOptions"/> (the same algorithm and
/// iteration count as real hashes). Replace this too if the password hasher is ever replaced.
/// </summary>
internal sealed class PasswordTimingEqualizer
{
    private static readonly AuthUser _nobody = new();

    private readonly PasswordHasher<AuthUser> _hasher;
    private readonly Lazy<string> _dummyHash;

    public PasswordTimingEqualizer(IOptions<PasswordHasherOptions> options)
    {
        _hasher = new PasswordHasher<AuthUser>(options);
        _dummyHash = new Lazy<string>(
            () => _hasher.HashPassword(_nobody, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Verifies <paramref name="password"/> against the dummy hash; the outcome is always a failure and ignored.</summary>
    public void VerifyAgainstDummy(string? password) =>
        _ = _hasher.VerifyHashedPassword(_nobody, _dummyHash.Value, password ?? string.Empty);
}
