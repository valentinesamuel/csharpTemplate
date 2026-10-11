using Acme.Service.BuildingBlocks.Persistence;
using Acme.Service.Modules.Auth.Domain;
using Acme.Service.Modules.Auth.External;
using Acme.Service.Modules.Auth.Mfa;
using Acme.Service.Modules.Auth.Sessions;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Acme.Service.Modules.Auth.Persistence;

/// <summary>
/// The Auth module context, schema <c>auth</c> (INV-06). Holds the Identity tables (renamed to
/// <c>users, roles, user_roles, user_claims, user_logins, user_tokens, role_claims</c>), the permission catalog,
/// sessions and refresh tokens, MFA sign-in challenges and the last accepted authenticator time steps, the one-time
/// external login codes, and the Data Protection key ring. The Identity EF stores work against it directly (they only need a
/// <see cref="DbContext"/>); the Identity model is mirrored in <c>Configurations/</c>.
/// </summary>
internal sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : ModuleDbContext(options), IDataProtectionKeyContext
{
    /// <summary>The module schema, equal to the module name.</summary>
    public const string SchemaName = "auth";

    public override string Schema => SchemaName;

    public DbSet<AuthUser> Users => Set<AuthUser>();

    public DbSet<AuthRole> Roles => Set<AuthRole>();

    public DbSet<IdentityUserRole<Guid>> UserRoles => Set<IdentityUserRole<Guid>>();

    public DbSet<IdentityUserToken<Guid>> UserTokens => Set<IdentityUserToken<Guid>>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<MfaChallenge> MfaChallenges => Set<MfaChallenge>();

    public DbSet<TotpTimeStep> TotpTimeSteps => Set<TotpTimeStep>();

    public DbSet<ExternalLoginCode> ExternalLoginCodes => Set<ExternalLoginCode>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthDbContext).Assembly);
}
