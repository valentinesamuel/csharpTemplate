using Acme.Service.Host.Commands;
using Acme.Service.Host.Modules;
using Shouldly;

namespace Acme.Service.Modules.Auth.Tests.Unit;

public sealed class GrantRoleVerbTests
{
    [Theory]
    [InlineData("")]
    [InlineData("--email a@b.io")]
    [InlineData("--role Admin")]
    [InlineData("--email --role Admin")]
    [InlineData("--email a@b.io --role")]
    [InlineData("--email a@b.io --role Admin extra")]
    [InlineData("--email a@b.io --role Admin --NoValue")]
    public async Task RunAsync_InvalidUsage_Returns2WithoutTouchingTheDatabase(string commandLine)
    {
        string[] arguments = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var exitCode = await GrantRoleVerb.RunAsync([GrantRoleVerb.Verb, .. arguments], HostModules.Create(), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(GrantRoleVerb.UsageExitCode);
    }

    [Fact]
    public void Verb_IsAuthGrantRole() => GrantRoleVerb.Verb.ShouldBe("auth:grant-role");
}
