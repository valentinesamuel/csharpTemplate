using Acme.Service.ArchitectureTests.Support;
using Shouldly;

namespace Acme.Service.ArchitectureTests;

/// <summary>R3 and R10 (INV-03, INV-05): no concrete provider types outside the Infra projects and the Host.</summary>
public sealed class ProviderIsolationTests
{
    [Fact]
    public void R3_ModulesAndBuildingBlocksCore_DoNotUseProviderTypes()
    {
        var result = ArchRules.NoConcreteProviderDependencies([.. SolutionAssemblies.ModulesAndContracts, SolutionAssemblies.BuildingBlocksCore]);

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public void R10_SrcProjects_OnlyHostReferencesInfra()
    {
        var result = ArchRules.OnlyHostReferencesInfra(RepositoryLayout.SrcProjects());

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public void R10_HostProject_ReferencesInfraProjects()
    {
        // Guards the rule itself: if csproj parsing found no Infra references at all, R10 would pass vacuously.
        var host = RepositoryLayout.SrcProjects().Single(p => SolutionAssemblies.Classify(p.Name) == ProjectKind.Host);

        host.ReferencedProjectNames.ShouldContain(name => SolutionAssemblies.Classify(name) == ProjectKind.Infra);
    }
}
