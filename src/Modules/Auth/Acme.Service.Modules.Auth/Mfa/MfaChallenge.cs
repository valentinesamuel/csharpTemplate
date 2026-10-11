using Acme.Service.Modules.Auth.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.Modules.Auth.Mfa;

/// <summary>
/// The second step of a sign-in with two-factor authentication, stored in <c>auth.mfa_challenges</c>. Created when the
/// password was correct (its id is the <c>jti</c> of the mfa token), consumed once by a valid code, and dead after
/// <see cref="MaxAttempts"/> wrong codes or at <see cref="ExpiresAt"/>.
/// </summary>
internal sealed class MfaChallenge
{
    /// <summary>Wrong codes a challenge tolerates; it is dead afterwards.</summary>
    public const int MaxAttempts = 5;

    public MfaChallenge(Guid jti, Guid userId, DateTimeOffset expiresAt)
    {
        Jti = jti;
        UserId = userId;
        ExpiresAt = expiresAt;
    }

    // EF Core materialization.
    private MfaChallenge()
    {
    }

    /// <summary>The <c>jti</c> of the mfa token.</summary>
    public Guid Jti { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When a valid code completed the sign-in; a consumed challenge never completes another.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>Wrong codes presented so far.</summary>
    public int Attempts { get; private set; }

    /// <summary>True when the challenge can still complete a sign-in at <paramref name="now"/>.</summary>
    public bool IsOpen(DateTimeOffset now) => ConsumedAt is null && Attempts < MaxAttempts && now < ExpiresAt;
}

/// <summary>
/// <c>auth.mfa_challenges(jti pk, user_id, expires_at, consumed_at, attempts)</c>. Deleting a user deletes its
/// challenges; the foreign key stays inside the <c>auth</c> schema (INV-06).
/// </summary>
internal sealed class MfaChallengeConfiguration : IEntityTypeConfiguration<MfaChallenge>
{
    public void Configure(EntityTypeBuilder<MfaChallenge> builder)
    {
        builder.ToTable("mfa_challenges");
        builder.HasKey(c => c.Jti);
        builder.Property(c => c.Jti).ValueGeneratedNever();
        builder.HasOne<AuthUser>().WithMany().HasForeignKey(c => c.UserId).IsRequired().OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => c.UserId);
    }
}
