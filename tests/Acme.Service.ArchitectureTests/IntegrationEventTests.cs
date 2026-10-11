using Acme.Service.ArchitectureTests.Support;
using Acme.Service.BuildingBlocks.Messaging;
using Shouldly;

namespace Acme.Service.ArchitectureTests;

/// <summary>R7 (INV-19): stable, well-formed and unique integration event names.</summary>
public sealed class IntegrationEventTests
{
    [Fact]
    public void R7_IntegrationEvents_HaveWellFormedUniqueNames()
    {
        var result = ArchRules.IntegrationEventsHaveStableUniqueNames(SolutionAssemblies.Src);

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public void R7_SrcAssemblies_DeclareIntegrationEvents()
    {
        // Guards the rule itself against passing vacuously.
        SolutionAssemblies.Src
            .SelectMany(SolutionAssemblies.TypesOf)
            .ShouldContain(t => !t.IsAbstract && typeof(IIntegrationEvent).IsAssignableFrom(t));
    }
}
