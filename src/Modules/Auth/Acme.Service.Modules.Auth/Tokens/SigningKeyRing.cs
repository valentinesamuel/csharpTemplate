using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Acme.Service.Modules.Auth.Tokens;

/// <summary>
/// The JWT signing key ring loaded from <c>Auth:Jwt:SigningKeys</c> (D-08). Signs with the single <c>Active</c> key
/// only; validates with <c>Active</c>, <c>Next</c> and <c>Retiring</c> keys, resolved strictly by <c>kid</c> (an
/// unknown <c>kid</c> resolves to no key, so the token is rejected; WH-15). Validation keys hold public parameters
/// only. Private key material never leaves this type (INV-11, INV-28).
/// </summary>
internal sealed class SigningKeyRing : IDisposable
{
    /// <summary>The only signing algorithm issued and accepted.</summary>
    public const string Algorithm = SecurityAlgorithms.RsaSha256;

    private readonly List<RSA> _ownedKeys = [];

    // The process-wide CryptoProviderFactory.Default caches signature providers by key thumbprint; a provider cached
    // for a disposed ring (same key material) would outlive its RSA instance. Each ring owns its factory and cache.
    private readonly CryptoProviderFactory _cryptoProviderFactory = new();
    private readonly Dictionary<string, SecurityKey[]> _validationKeys = new(StringComparer.Ordinal);

    public SigningKeyRing(IOptions<JwtOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var keys = options.Value.SigningKeys;
        var entries = new List<SigningKeyEntry>();
        SigningCredentials? signing = null;
        try
        {
            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                var path = FormattableString.Invariant($"{JwtOptions.SectionName}:SigningKeys:{i}");
                if (!SigningKeyOptions.IsValidKid(key.Kid) || !key.TryGetStatus(out var status))
                {
                    throw new InvalidOperationException($"{path} is invalid; see the startup validation errors.");
                }

                var kid = key.Kid!;
                var privateKey = key.TryLoadPrivateKey(path, out var error)
                    ?? throw new InvalidOperationException(error);
                _ownedKeys.Add(privateKey);

                var publicParameters = privateKey.ExportParameters(includePrivateParameters: false);
                var publicKey = RSA.Create(publicParameters);
                _ownedKeys.Add(publicKey);

                var validationKey = new RsaSecurityKey(publicKey) { KeyId = kid, CryptoProviderFactory = _cryptoProviderFactory };
                if (!_validationKeys.TryAdd(kid, [validationKey]))
                {
                    throw new InvalidOperationException($"{path}:Kid '{kid}' is not unique.");
                }

                if (status == SigningKeyStatus.Active)
                {
                    if (signing is not null)
                    {
                        throw new InvalidOperationException($"{JwtOptions.SectionName}:SigningKeys has more than one Active key.");
                    }

                    var signingKey = new RsaSecurityKey(privateKey) { KeyId = kid, CryptoProviderFactory = _cryptoProviderFactory };
                    signing = new SigningCredentials(signingKey, Algorithm) { CryptoProviderFactory = _cryptoProviderFactory };
                }

                entries.Add(new SigningKeyEntry(kid, status, publicParameters));
            }

            SigningCredentials = signing
                ?? throw new InvalidOperationException($"{JwtOptions.SectionName}:SigningKeys has no Active key.");
        }
        catch
        {
            Dispose();
            throw;
        }

        Keys = [.. entries.OrderBy(e => e.Status)];
        JwksJson = BuildJwks(Keys);
    }

    /// <summary>Credentials of the <c>Active</c> key (RS256), used for every token issued.</summary>
    public SigningCredentials SigningCredentials { get; }

    /// <summary>The <c>kid</c> of the <c>Active</c> key.</summary>
    public string ActiveKid => SigningCredentials.Key.KeyId;

    /// <summary>Every key of the ring (public parameters only): Active first, then Next, then Retiring.</summary>
    public IReadOnlyList<SigningKeyEntry> Keys { get; }

    /// <summary>The JWKS document (RFC 7517) publishing the public part of every key.</summary>
    public string JwksJson { get; }

    /// <summary>
    /// The validation key for <paramref name="kid"/>, or none. Never falls back to trying all keys, so a token
    /// without a <c>kid</c> or with an unknown one cannot validate (WH-15, INV-13).
    /// </summary>
    public IEnumerable<SecurityKey> ResolveValidationKeys(string? kid) =>
        kid is not null && _validationKeys.TryGetValue(kid, out var key) ? key : [];

    public void Dispose()
    {
        foreach (var key in _ownedKeys)
        {
            key.Dispose();
        }

        _ownedKeys.Clear();
        (_cryptoProviderFactory.CryptoProviderCache as IDisposable)?.Dispose();
    }

    private static string BuildJwks(IReadOnlyList<SigningKeyEntry> keys)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("keys");
            foreach (var key in keys)
            {
                writer.WriteStartObject();
                writer.WriteString("kty", "RSA");
                writer.WriteString("use", "sig");
                writer.WriteString("alg", Algorithm);
                writer.WriteString("kid", key.Kid);
                writer.WriteString("n", Base64Url.EncodeToString(key.PublicParameters.Modulus));
                writer.WriteString("e", Base64Url.EncodeToString(key.PublicParameters.Exponent));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>A key of the ring: its id, status and public parameters (modulus and exponent only).</summary>
internal sealed record SigningKeyEntry(string Kid, SigningKeyStatus Status, RSAParameters PublicParameters);
