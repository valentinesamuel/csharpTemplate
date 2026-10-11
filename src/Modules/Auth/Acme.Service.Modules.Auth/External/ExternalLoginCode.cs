using System.Buffers.Text;
using System.Security.Cryptography;
using Acme.Service.Modules.Auth.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.Modules.Auth.External;

/// <summary>
/// The one-time code that hands a completed external login to the client (D-13), stored in
/// <c>auth.external_login_codes</c>. The callback redirects with the code (32 random bytes, base64url); only its SHA-256
/// hash is stored (INV-28). <c>POST /external/exchange</c> consumes it once, before <see cref="ExpiresAt"/>
/// (<see cref="Lifetime"/> after issue). The code is never a bearer token (INV-14).
/// </summary>
internal sealed class ExternalLoginCode
{
    /// <summary>Random bytes in a code.</summary>
    public const int CodeBytes = 32;

    /// <summary>How long a code can be exchanged.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public ExternalLoginCode(Guid userId, byte[] codeHash, DateTimeOffset createdAt)
    {
        Id = Guid.CreateVersion7(createdAt);
        UserId = userId;
        CodeHash = codeHash;
        ExpiresAt = createdAt + Lifetime;
    }

    // EF Core materialization.
    private ExternalLoginCode()
    {
        CodeHash = [];
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>SHA-256 of the decoded code.</summary>
    public byte[] CodeHash { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the code was exchanged; a consumed code never exchanges again.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>A new random code (base64url, no padding) and the SHA-256 hash to store.</summary>
    public static (string Code, byte[] Hash) NewCode()
    {
        Span<byte> bytes = stackalloc byte[CodeBytes];
        RandomNumberGenerator.Fill(bytes);
        return (Base64Url.EncodeToString(bytes), SHA256.HashData(bytes));
    }

    /// <summary>SHA-256 of the decoded code; false when <paramref name="code"/> is not a well-formed code.</summary>
    public static bool TryHash(string? code, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrEmpty(code)
            || code.Length != Base64Url.GetEncodedLength(CodeBytes)
            || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[CodeBytes];
        if (!Base64Url.TryDecodeFromChars(code, bytes, out var written) || written != CodeBytes)
        {
            return false;
        }

        hash = SHA256.HashData(bytes);
        return true;
    }
}

/// <summary>
/// <c>auth.external_login_codes(id pk, user_id, code_hash unique, expires_at, consumed_at)</c>. Deleting a user deletes
/// its codes; the foreign key stays inside the <c>auth</c> schema (INV-06).
/// </summary>
internal sealed class ExternalLoginCodeConfiguration : IEntityTypeConfiguration<ExternalLoginCode>
{
    public void Configure(EntityTypeBuilder<ExternalLoginCode> builder)
    {
        builder.ToTable("external_login_codes");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.CodeHash).IsRequired();
        builder.HasIndex(c => c.CodeHash).IsUnique();
        builder.HasIndex(c => c.UserId);
        builder.HasOne<AuthUser>().WithMany().HasForeignKey(c => c.UserId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}
