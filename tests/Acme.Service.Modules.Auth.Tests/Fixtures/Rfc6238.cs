using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

namespace Acme.Service.Modules.Auth.Tests.Fixtures;

/// <summary>
/// An authenticator app in the tests: RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30-second steps) over a Base32 shared key,
/// written independently of the production verifier.
/// </summary>
internal static class Rfc6238
{
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>The code an authenticator app shows for <paramref name="sharedKey"/> at <paramref name="time"/>.</summary>
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "RFC 6238 TOTP is defined over HMAC-SHA1.")]
    public static string Code(string sharedKey, DateTimeOffset time)
    {
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, time.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(Base32(sharedKey), counter);
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>A six-digit code different from every code valid around <paramref name="time"/>.</summary>
    public static string WrongCode(string sharedKey, DateTimeOffset time)
    {
        var valid = new[] { Code(sharedKey, time - Step), Code(sharedKey, time), Code(sharedKey, time + Step) };
        for (var candidate = 0; ; candidate++)
        {
            var code = candidate.ToString("D6", CultureInfo.InvariantCulture);
            if (!valid.Contains(code))
            {
                return code;
            }
        }
    }

    private static byte[] Base32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(value.Trim('=').ToUpperInvariant()
            .Select(c => Convert.ToString(alphabet.IndexOf(c, StringComparison.Ordinal), 2).PadLeft(5, '0')));
        return [.. Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2))];
    }
}
