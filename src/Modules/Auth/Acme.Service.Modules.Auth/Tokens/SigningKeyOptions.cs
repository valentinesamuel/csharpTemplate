using System.Security.Cryptography;
using System.Text;

namespace Acme.Service.Modules.Auth.Tokens;

/// <summary>
/// The state of a signing key in the ring (D-08). Exactly one key is <see cref="Active"/> and signs new tokens;
/// <see cref="Next"/> keys are published ahead of a rotation and <see cref="Retiring"/> keys stay valid until the
/// tokens they signed have expired. All three validate tokens and appear in the JWKS document.
/// </summary>
internal enum SigningKeyStatus
{
    Active,
    Next,
    Retiring,
}

/// <summary>
/// One entry of <c>Auth:Jwt:SigningKeys</c> (§0.7): <c>{Kid, Status=Active|Next|Retiring, PrivateKeyPath|PrivateKeyBase64}</c>.
/// Exactly one of <see cref="PrivateKeyPath"/> (a mounted file holding a PEM private key, for example a Kubernetes
/// Secret) and <see cref="PrivateKeyBase64"/> (the base64 of a PEM private key, for environment variables and
/// user-secrets) is set. Never logged or echoed (INV-11, INV-28).
/// </summary>
internal sealed class SigningKeyOptions
{
    /// <summary>Maximum length of <see cref="Kid"/>.</summary>
    public const int KidMaxLength = 64;

    /// <summary>Smallest accepted RSA modulus, in bits.</summary>
    public const int MinimumKeySizeBits = 2048;

    /// <summary>Key id, published as <c>kid</c> in the JWKS document and in every token header it signs.</summary>
    public string? Kid { get; set; }

    /// <summary><c>Active</c>, <c>Next</c> or <c>Retiring</c>.</summary>
    public string? Status { get; set; }

    /// <summary>Path of a file holding the PEM private key.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Base64 of the PEM private key text.</summary>
    public string? PrivateKeyBase64 { get; set; }

    /// <summary>Parses <see cref="Status"/> (case-insensitive, names only).</summary>
    public bool TryGetStatus(out SigningKeyStatus status)
    {
        status = default;
        return !string.IsNullOrWhiteSpace(Status)
            && !char.IsAsciiDigit(Status.Trim()[0])
            && !Status.Contains(',', StringComparison.Ordinal)
            && Enum.TryParse(Status.Trim(), ignoreCase: true, out status)
            && Enum.IsDefined(status);
    }

    /// <summary>True when <paramref name="kid"/> is 1 to <see cref="KidMaxLength"/> characters of <c>[A-Za-z0-9._-]</c>.</summary>
    public static bool IsValidKid(string? kid) =>
        !string.IsNullOrEmpty(kid)
        && kid.Length <= KidMaxLength
        && kid.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>
    /// Loads the RSA private key. On failure returns null and a message naming the configuration key
    /// (<paramref name="keyPath"/>, for example <c>Auth:Jwt:SigningKeys:0</c>); messages never contain key material.
    /// </summary>
    public RSA? TryLoadPrivateKey(string keyPath, out string? error)
    {
        var hasPath = !string.IsNullOrWhiteSpace(PrivateKeyPath);
        var hasBase64 = !string.IsNullOrWhiteSpace(PrivateKeyBase64);
        if (hasPath == hasBase64)
        {
            error = $"{keyPath} must set exactly one of PrivateKeyPath or PrivateKeyBase64.";
            return null;
        }

        string pem;
        if (hasPath)
        {
            try
            {
                pem = File.ReadAllText(PrivateKeyPath!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = $"{keyPath}:PrivateKeyPath could not be read.";
                return null;
            }
        }
        else
        {
            try
            {
                pem = Encoding.UTF8.GetString(Convert.FromBase64String(PrivateKeyBase64!.Trim()));
            }
            catch (FormatException)
            {
                error = $"{keyPath}:PrivateKeyBase64 is not valid base64 (expected the base64 of a PEM private key).";
                return null;
            }
        }

        var source = hasPath ? $"{keyPath}:PrivateKeyPath" : $"{keyPath}:PrivateKeyBase64";
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);

            // A public-key PEM imports without error but cannot sign; require the private parameters.
            _ = rsa.ExportParameters(includePrivateParameters: true);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            error = $"{source} does not hold an RSA private key in PEM format.";
            return null;
        }

        if (rsa.KeySize < MinimumKeySizeBits)
        {
            var bits = rsa.KeySize;
            rsa.Dispose();
            error = $"{source} holds a {bits}-bit RSA key; at least {MinimumKeySizeBits} bits are required.";
            return null;
        }

        error = null;
        return rsa;
    }
}
