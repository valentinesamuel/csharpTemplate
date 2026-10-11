using Acme.Service.BuildingBlocks.Persistence.DesignTime;
using Microsoft.EntityFrameworkCore;

namespace Acme.Service.Modules.Auth.Persistence;

/// <summary>Design-time factory for <c>dotnet ef</c>. Internal: EF tools discover non-public factories (WH-09).</summary>
internal sealed class AuthDesignTimeFactory : ModuleDesignTimeFactoryBase<AuthDbContext>
{
    protected override string ModuleKey => AuthDbContext.SchemaName;

    protected override AuthDbContext CreateContext(DbContextOptions<AuthDbContext> options) => new(options);
}
