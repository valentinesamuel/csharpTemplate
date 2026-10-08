using System.Reflection;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void Load_BuildingBlocksAssembly_ReturnsExpectedName()
    {
        var assembly = Assembly.Load("Acme.Service.BuildingBlocks");

        assembly.GetName().Name.ShouldBe("Acme.Service.BuildingBlocks");
    }
}
