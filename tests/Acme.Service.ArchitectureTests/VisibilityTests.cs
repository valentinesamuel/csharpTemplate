using Acme.Service.ArchitectureTests.Support;
using Shouldly;

namespace Acme.Service.ArchitectureTests;

/// <summary>R5 and R8 (INV-02): module internals are internal by default.</summary>
public sealed class VisibilityTests
{
    [Fact]
    public void R5_ModuleAssemblies_ExposeOnlyTheirModuleType()
    {
        var result = ArchRules.OnlyModuleTypeIsPublic(SolutionAssemblies.Modules);

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public void R8_UseCases_AreNotPublic()
    {
        var result = ArchRules.UseCasesAreNotPublic(SolutionAssemblies.Src);

        result.Success.ShouldBeTrue(result.Message);
    }
}
