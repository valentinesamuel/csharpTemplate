using Acme.Service.Modules.Auth.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.Modules.Auth.Sessions;

/// <summary>
/// <c>auth.sessions(id, user_id, created_at, absolute_expires_at, revoked_at, revoked_reason, amr)</c>. Deleting a
/// user deletes its sessions; the foreign key stays inside the <c>auth</c> schema (INV-06).
/// </summary>
internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.RevokedReason).HasMaxLength(Session.RevokedReasonMaxLength);
        builder.Property(s => s.Amr).IsRequired();
        builder.HasOne<AuthUser>().WithMany().HasForeignKey(s => s.UserId).IsRequired().OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => s.UserId);
    }
}

/// <summary>
/// <c>auth.refresh_tokens(id, session_id, token_hash bytea unique, created_at, expires_at, used_at, replaced_by_id,
/// created_ip, user_agent)</c>. Deleting a session deletes its tokens.
/// </summary>
internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TokenHash).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.Property(t => t.CreatedIp).HasMaxLength(RefreshToken.CreatedIpMaxLength);
        builder.Property(t => t.UserAgent).HasMaxLength(RefreshToken.UserAgentMaxLength);
        builder.HasOne<Session>().WithMany().HasForeignKey(t => t.SessionId).IsRequired().OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<RefreshToken>().WithMany().HasForeignKey(t => t.ReplacedById).OnDelete(DeleteBehavior.SetNull);
    }
}
