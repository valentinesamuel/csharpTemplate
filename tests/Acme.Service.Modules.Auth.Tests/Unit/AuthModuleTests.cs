using Acme.Service.Host.Modules;
using Acme.Service.Modules.Auth.Contracts;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Unit;

public sealed class AuthModuleTests
{
    [Fact]
    public void Assembly_OnlyPublicTypeIsAuthModule()
    {
        // INV-02. The design-time factory is internal too (WH-09 holds).
        typeof(AuthModule).Assembly.GetExportedTypes().ShouldBe([typeof(AuthModule)]);
    }

    [Fact]
    public void Name_IsTheAuthSchema() => new AuthModule().Name.ShouldBe("auth");

    [Fact]
    public void Permissions_MatchThePlanDefaults()
    {
        // Plan §0.8.
        new AuthModule().Permissions
            .Select(p => $"{p.Name}:{string.Join(",", p.DefaultRoles)}")
            .ShouldBe([$"{AuthPermissions.UsersRead}:Admin,Staff", $"{AuthPermissions.UsersManage}:Admin", $"{AuthPermissions.RolesRead}:Admin,Staff"]);
    }

    [Fact]
    public void HostModules_RegistersAuthFirst() => HostModules.Create()[0].ShouldBeOfType<AuthModule>();
}
