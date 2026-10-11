using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.Modules.Auth.Mfa;

/// <summary>
/// Verifies authenticator (TOTP) codes and accepts each time step at most once per user (WH-11, INV-28).
/// </summary>
/// <remarks>
/// <para>
/// The codes are those of Identity's <see cref="AuthenticatorTokenProvider{TUser}"/> (RFC 6238: HMAC-SHA1 over the
/// Base32 authenticator key, 6 digits, 30-second steps), but verified here because that provider neither reports which
/// time step matched (needed to reject replays) nor reads the injected <see cref="TimeProvider"/> (it uses the system
/// clock). The tolerated drift is one step either way (Identity accepts two).
/// </para>
/// <para>
/// A matching code is accepted only if its time step is greater than the last step accepted for the user, raised
/// atomically in <c>auth.totp_time_steps</c> (a conditional upsert), so the same code, or an older one, is refused
/// even by concurrent requests. Codes are compared in constant time and never logged.
/// </para>
/// </remarks>
internal sealed class TotpReplayGuard(AuthDbContext context, UserManager<AuthUser> userManager, TimeProvider timeProvider)
{
    /// <summary>Digits of a code.</summary>
    public const int Digits = 6;

    /// <summary>Time steps accepted before and after the current one.</summary>
    public const int AllowedDriftSteps = 1;

    /// <summary>Length of a time step.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True when <paramref name="code"/> is a valid code of the user's authenticator key for a time step not accepted
    /// before; that step is then recorded. Joins an open Auth transaction.
    /// </summary>
    public async Task<bool> TryAcceptAsync(AuthUser user, string? code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!IsWellFormed(code))
        {
            return false;
        }

        var key = await userManager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        if (key is null || !TryDecodeBase32(key, out var keyBytes))
        {
            return false;
        }

        var matched = MatchTimeStep(keyBytes, code!, timeProvider.GetUtcNow());
        CryptographicOperations.ZeroMemory(keyBytes);
        if (matched is not { } step)
        {
            return false;
        }

        // Raise the last accepted step only if it is lower: 1 row when accepted, 0 for a replayed or older step.
        var accepted = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO auth.totp_time_steps (user_id, last_time_step) VALUES ({user.Id}, {step})
            ON CONFLICT (user_id) DO UPDATE SET last_time_step = EXCLUDED.last_time_step
            WHERE auth.totp_time_steps.last_time_step < EXCLUDED.last_time_step
            """,
            cancellationToken).ConfigureAwait(false);
        return accepted == 1;
    }

    /// <summary>Forgets the last accepted step (the authenticator key was replaced). Joins an open Auth transaction.</summary>
    public Task ResetAsync(Guid userId, CancellationToken cancellationToken) =>
        context.TotpTimeSteps.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);

    /// <summary>The RFC 6238 time step of <paramref name="time"/>.</summary>
    internal static long TimeStepOf(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)Period.TotalSeconds;

    /// <summary>The <see cref="Digits"/>-digit code of <paramref name="key"/> at <paramref name="timeStep"/> (RFC 4226 truncation).</summary>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 TOTP as implemented by authenticator apps and Identity uses HMAC-SHA1; HMAC-SHA1 is not affected by SHA-1 collisions.")]
    internal static string ComputeCode(ReadOnlySpan<byte> key, long timeStep)
    {
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = (BinaryPrimitives.ReadInt32BigEndian(hash[offset..]) & 0x7FFFFFFF) % 1_000_000;
        return binary.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>Decodes an RFC 4648 Base32 string (case-insensitive, padding and spaces ignored).</summary>
    internal static bool TryDecodeBase32(string value, out byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var output = new List<byte>(value.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var c in value)
        {
            if (c is '=' or ' ')
            {
                continue;
            }

            var index = alphabet.IndexOf(char.ToUpperInvariant(c), StringComparison.Ordinal);
            if (index < 0)
            {
                bytes = [];
                return false;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        bytes = [.. output];
        return bytes.Length > 0;
    }

    private static bool IsWellFormed(string? code) => code is { Length: Digits } && code.All(char.IsAsciiDigit);

    // Checks every step of the window (no early exit) and returns the latest match.
    private static long? MatchTimeStep(byte[] key, string code, DateTimeOffset now)
    {
        var expected = Encoding.ASCII.GetBytes(code);
        var current = TimeStepOf(now);
        long? matched = null;
        for (var step = current - AllowedDriftSteps; step <= current + AllowedDriftSteps; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ComputeCode(key, step)), expected))
            {
                matched = step;
            }
        }

        return matched;
    }
}

/// <summary>The last authenticator time step accepted for a user (<c>auth.totp_time_steps</c>).</summary>
internal sealed class TotpTimeStep
{
    // EF Core materialization; rows are written by TotpReplayGuard's upsert.
    private TotpTimeStep()
    {
    }

    public Guid UserId { get; private set; }

    public long LastTimeStep { get; private set; }
}

/// <summary>
/// <c>auth.totp_time_steps(user_id pk, last_time_step)</c>, a table of its own rather than a column of
/// <c>auth.users</c>: Identity's user store rewrites every column of the user row on update, which would restore a
/// stale step. Deleting a user deletes its row.
/// </summary>
internal sealed class TotpTimeStepConfiguration : IEntityTypeConfiguration<TotpTimeStep>
{
    public void Configure(EntityTypeBuilder<TotpTimeStep> builder)
    {
        builder.ToTable("totp_time_steps");
        builder.HasKey(s => s.UserId);
        builder.Property(s => s.UserId).ValueGeneratedNever();
        builder.HasOne<AuthUser>().WithMany().HasForeignKey(s => s.UserId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}
