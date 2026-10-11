using Acme.Service.Modules.Auth.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.Modules.Auth.Persistence.Configurations;

/// <summary>
/// <c>auth.users</c>. Mirrors the Identity EF model (<c>IdentityUserContext</c>), because <see cref="AuthDbContext"/>
/// derives from <c>ModuleDbContext</c> rather than <c>IdentityDbContext</c>.
/// </summary>
internal sealed class AuthUserConfiguration : IEntityTypeConfiguration<AuthUser>
{
    public void Configure(EntityTypeBuilder<AuthUser> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.HasIndex(u => u.NormalizedUserName).IsUnique();
        builder.HasIndex(u => u.NormalizedEmail);

        builder.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
        builder.Property(u => u.UserName).HasMaxLength(IdentityModelLimits.Default);
        builder.Property(u => u.NormalizedUserName).HasMaxLength(IdentityModelLimits.Default);
        builder.Property(u => u.Email).HasMaxLength(IdentityModelLimits.Default);
        builder.Property(u => u.NormalizedEmail).HasMaxLength(IdentityModelLimits.Default);
        builder.Property(u => u.PhoneNumber).HasMaxLength(IdentityModelLimits.Default);
        builder.Property(u => u.DisplayName).HasMaxLength(AuthUser.DisplayNameMaxLength);
        builder.Property(u => u.CreatedAt);

        builder.HasMany<IdentityUserClaim<Guid>>().WithOne().HasForeignKey(c => c.UserId).IsRequired();
        builder.HasMany<IdentityUserLogin<Guid>>().WithOne().HasForeignKey(l => l.UserId).IsRequired();
        builder.HasMany<IdentityUserToken<Guid>>().WithOne().HasForeignKey(t => t.UserId).IsRequired();
        builder.HasMany<IdentityUserRole<Guid>>().WithOne().HasForeignKey(r => r.UserId).IsRequired();
    }
}
