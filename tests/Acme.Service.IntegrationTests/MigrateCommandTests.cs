using Acme.Service.Host.Commands;
using Acme.Service.Testing.Containers;
using Shouldly;

namespace Acme.Service.IntegrationTests;

public sealed class MigrateCommandTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_ListWithNoModules_PrintsNoContextsAndReturns0()
    {
        var connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        using var output = new StringWriter();

        var exitCode = await MigrateCommand.RunAsync(
            ["migrate", "--list", $"--ConnectionStrings:PostgresMigrations={connectionString}"],
            [],
            output,
            TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain("no module contexts registered");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_ApplyWithNoModules_Returns0()
    {
        using var output = new StringWriter();

        var exitCode = await MigrateCommand.RunAsync(["migrate"], [], output, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain("no module contexts registered");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_UnknownOption_Returns2()
    {
        using var output = new StringWriter();

        var exitCode = await MigrateCommand.RunAsync(["migrate", "--force"], [], output, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(MigrateCommand.UsageExitCode);
    }
}
