using Acme.Service.Modules.Auth.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Acme.Service.Modules.Auth.Persistence.Configurations;

/// <summary><c>auth.permissions(name pk, description, module)</c>.</summary>
internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("permissions");
        builder.HasKey(p => p.Name);
        builder.Property(p => p.Name).HasMaxLength(Permission.NameMaxLength);
        builder.Property(p => p.Description).HasMaxLength(Permission.DescriptionMaxLength).IsRequired();
        builder.Property(p => p.Module).HasMaxLength(Permission.ModuleMaxLength).IsRequired();
    }
}

/// <summary>
/// <c>auth.role_permissions(role_id, permission_name)</c>. Deleting a role or a permission deletes its grants.
/// </summary>
internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");
        builder.HasKey(rp => new { rp.RoleId, rp.PermissionName });
        builder.Property(rp => rp.PermissionName).HasMaxLength(Permission.NameMaxLength);
        builder.HasOne<Permission>().WithMany().HasForeignKey(rp => rp.PermissionName).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}
