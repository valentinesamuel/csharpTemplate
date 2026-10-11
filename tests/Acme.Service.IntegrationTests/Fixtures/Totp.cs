using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

namespace Acme.Service.IntegrationTests.Fixtures;

/// <summary>
/// An authenticator app (RFC 6238: HMAC-SHA1, 6 digits, 30-second steps over a Base32 key), written independently of
/// the production verifier. The server accepts one step of drift either way and each step once per user, so with the
/// real clock a test enables MFA with the current step's code and signs in with the next step's code.
/// </summary>
internal static class Totp
{
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>The code for <paramref name="sharedKey"/> at <paramref name="time"/>.</summary>
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

    private static byte[] Base32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(value.Trim('=').ToUpperInvariant()
            .Select(c => Convert.ToString(alphabet.IndexOf(c, StringComparison.Ordinal), 2).PadLeft(5, '0')));
        return [.. Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2))];
    }
}
