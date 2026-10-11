using System.Security.Cryptography;
using System.Text;

namespace Acme.Service.Testing.Security;

/// <summary>
/// JWT signing keys for test hosts, generated at runtime (INV-11: no key material in the repository). The default key
/// is created once per test process and used as the single <c>Active</c> key of every <see cref="ServiceWebFactory"/>.
/// </summary>
public static class TestSigningKeys
{
    /// <summary>The <c>kid</c> of the default test key.</summary>
    public const string DefaultKid = "test-active";

    private static readonly Lazy<string> _default = new(() => CreatePrivateKeyBase64(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Base64 of the PEM private key of the default test key (an RSA-2048 key).</summary>
    public static string DefaultPrivateKeyBase64 => _default.Value;

    /// <summary>Creates a new RSA key and returns the base64 of its PKCS#8 PEM, the <c>PrivateKeyBase64</c> format.</summary>
    public static string CreatePrivateKeyBase64(int keySizeInBits = 2048)
    {
        using var rsa = RSA.Create(keySizeInBits);
        return ToBase64Pem(rsa);
    }

    /// <summary>The base64 of the PKCS#8 PEM of <paramref name="rsa"/>'s private key.</summary>
    public static string ToBase64Pem(RSA rsa)
    {
        ArgumentNullException.ThrowIfNull(rsa);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportPkcs8PrivateKeyPem()));
    }

    /// <summary>Configuration entries (<c>:</c> separator) for key <paramref name="index"/> of <c>Auth:Jwt:SigningKeys</c>.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> Entry(int index, string kid, string status, string privateKeyBase64)
    {
        var prefix = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Auth:Jwt:SigningKeys:{index}");
        yield return new($"{prefix}:Kid", kid);
        yield return new($"{prefix}:Status", status);
        yield return new($"{prefix}:PrivateKeyBase64", privateKeyBase64);
    }
}
