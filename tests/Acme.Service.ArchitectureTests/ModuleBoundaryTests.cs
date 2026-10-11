using Acme.Service.ArchitectureTests.Support;
using Shouldly;

namespace Acme.Service.ArchitectureTests;

/// <summary>R1, R2, R6 (INV-01) over the real src/ assemblies, plus the plan §0.3 project reference matrix.</summary>
public sealed class ModuleBoundaryTests
{
    [Fact]
    public void Discovery_SrcModules_FindsEveryModuleAndItsContracts()
    {
        SolutionAssemblies.Modules.ShouldNotBeEmpty();
        foreach (var module in SolutionAssemblies.Modules)
        {
            var moduleName = SolutionAssemblies.ModuleNameOf(SolutionAssemblies.NameOf(module))!;
            SolutionAssemblies.Contracts
                .Select(SolutionAssemblies.NameOf)
                .ShouldContain(SolutionAssemblies.ContractsNameOf(moduleName));
        }
    }

    [Fact]
    public void R1_Modules_DependOnOtherModulesOnlyThroughContracts()
    {
        var result = ArchRules.ModulesDependOnlyOnOtherModulesContracts(SolutionAssemblies.ModulesAndContracts);

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public void R2_Contracts_DependOnlyOnBuildingBlocksAndBcl()
    {
        var result = ArchRules.ContractsDependOnlyOnBuildingBlocksAndBcl(SolutionAssemblies.Contracts);

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public void R6_BuildingBlocks_DoNotDependOnModules()
    {
        var result = ArchRules.BuildingBlocksDoNotDependOnModules(SolutionAssemblies.AllBuildingBlocks);

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public void ReferenceMatrix_SrcProjects_FollowPlanMatrix()
    {
        var result = ArchRules.ProjectReferencesFollowMatrix(RepositoryLayout.SrcProjects(), RepositoryLayout.SrcDirectory);

        result.Success.ShouldBeTrue(result.Message);
    }
}
