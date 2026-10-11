using Acme.Service.ArchitectureTests.Support;
using Shouldly;

namespace Acme.Service.ArchitectureTests;

/// <summary>R4 (INV-04, INV-05) and the INV-06 single-schema check.</summary>
public sealed class PersistenceConfinementTests
{
    [Fact]
    public void R4_NonAuthModules_ConfineEfCoreToPersistenceNamespaces()
    {
        var result = ArchRules.EfCoreConfinedToPersistence(SolutionAssemblies.Modules);

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public void Inv06_ModuleDbContexts_MapOnlyTheirOwnSchema()
    {
        var result = ArchRules.ModuleContextsMapSingleSchema(SolutionAssemblies.Modules);

        result.Success.ShouldBeTrue(result.Message);
    }
}
