using Acme.Service.BuildingBlocks.Security;
using Acme.Service.Modules.Auth.Seeding;
using Acme.Service.Modules.Auth.Tests.Fixtures;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Unit;

public sealed class PermissionCatalogTests
{
    [Fact]
    public void Build_AuthAndAnotherModule_CollectsBothWithOwners()
    {
        var catalog = PermissionCatalog.Build([new AuthModule(), new TestModule(new PermissionDefinition("widgets.read", "Read widgets.", ["Customer"]))]);

        catalog.Entries.Select(e => $"{e.Module}:{e.Definition.Name}")
            .ShouldBe(["auth:users.read", "auth:users.manage", "auth:roles.read", "widgets:widgets.read"]);
        catalog.DefaultPermissionsOf("Customer").ShouldBe(["widgets.read"]);
        catalog.DefaultPermissionsOf("Admin").ShouldBe(["users.read", "users.manage", "roles.read"], ignoreOrder: true);
        catalog.DefaultPermissionsOf("Staff").ShouldBe(["users.read", "roles.read"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("users.read", "is declared by both")]
    [InlineData("Widgets.Read", "must be lower-case dotted")]
    [InlineData("widgets", "must be lower-case dotted")]
    [InlineData("widgets..read", "must be lower-case dotted")]
    public void Build_InvalidOrDuplicateName_Throws(string name, string expected)
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => PermissionCatalog.Build([new AuthModule(), new TestModule(new PermissionDefinition(name, "Description.", []))]));

        exception.Message.ShouldContain(expected);
    }

    [Fact]
    public void Build_UnknownDefaultRole_Throws()
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => PermissionCatalog.Build([new TestModule(new PermissionDefinition("widgets.read", "Read widgets.", ["Root"]))]));

        exception.Message.ShouldContain("default role 'Root', which is not a seed role");
    }

    [Fact]
    public void Build_MissingDescription_Throws()
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => PermissionCatalog.Build([new TestModule(new PermissionDefinition("widgets.read", " ", []))]));

        exception.Message.ShouldContain("needs a description");
    }
}
