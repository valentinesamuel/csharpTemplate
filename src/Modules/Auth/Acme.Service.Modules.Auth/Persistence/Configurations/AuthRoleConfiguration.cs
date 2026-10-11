using Acme.Service.Modules.Auth.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.Modules.Auth.Persistence.Configurations;

/// <summary><c>auth.roles</c>. Mirrors the Identity EF model (<c>IdentityDbContext</c>).</summary>
internal sealed class AuthRoleConfiguration : IEntityTypeConfiguration<AuthRole>
{
    public void Configure(EntityTypeBuilder<AuthRole> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.HasIndex(r => r.NormalizedName).IsUnique();

        builder.Property(r => r.ConcurrencyStamp).IsConcurrencyToken();
        builder.Property(r => r.Name).HasMaxLength(IdentityModelLimits.Default);
        builder.Property(r => r.NormalizedName).HasMaxLength(IdentityModelLimits.Default);

        builder.HasMany<IdentityUserRole<Guid>>().WithOne().HasForeignKey(ur => ur.RoleId).IsRequired();
        builder.HasMany<IdentityRoleClaim<Guid>>().WithOne().HasForeignKey(rc => rc.RoleId).IsRequired();
        builder.HasMany(r => r.Permissions).WithOne().HasForeignKey(rp => rp.RoleId).IsRequired();
    }
}
