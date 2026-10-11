using System.Reflection;
using Acme.Service.ArchitectureTests.Support;
using Acme.Service.Modules.Orders.Illegal;
using Shouldly;

namespace Acme.Service.ArchitectureTests;

/// <summary>
/// Proves R1 detects an illegal module reference: the fixture assembly references the Auth module's non-Contracts
/// assembly and uses <c>AuthModule</c> from another module's namespace, so R1 over [fixture + Auth] must fail.
/// </summary>
public sealed class IllegalReferenceProofTests
{
    private const string FixtureProjectName = "Acme.Service.ArchitectureTests.Fixtures.IllegalReference";
    private const string ArchitectureTestsProjectName = "Acme.Service.ArchitectureTests";

    private static Assembly FixtureAssembly => typeof(IllegalAuthModuleUsage).Assembly;

    private static Assembly AuthAssembly =>
        SolutionAssemblies.Modules.Single(a => SolutionAssemblies.ModuleNameOf(SolutionAssemblies.NameOf(a)) == "Auth");

    [Fact]
    public void R1_FixtureReferencingAuthInternals_FailsNamingOffendingType()
    {
        var result = ArchRules.ModulesDependOnlyOnOtherModulesContracts([FixtureAssembly, AuthAssembly]);

        result.Success.ShouldBeFalse();
        result.RuleId.ShouldBe("R1");
        result.OffendingNames.ShouldContain(typeof(IllegalAuthModuleUsage).FullName!);
        result.Message.ShouldContain("R1");
        result.Message.ShouldContain(typeof(IllegalAuthModuleUsage).FullName!);
        result.Message.ShouldContain(SolutionAssemblies.NameOf(AuthAssembly));
    }

    [Fact]
    public void R1_AuthAlone_PassesSoTheFailureComesFromTheFixture()
    {
        var result = ArchRules.ModulesDependOnlyOnOtherModulesContracts([AuthAssembly]);

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public void Fixture_IsReferencedOnlyByArchitectureTests()
    {
        var projects = RepositoryLayout.AllProjects();
        projects.Select(p => p.Name).ShouldContain(FixtureProjectName);

        var result = ArchRules.ProjectReferencedOnlyBy(projects, FixtureProjectName, ArchitectureTestsProjectName);

        result.Success.ShouldBeTrue(result.Message);
    }

    [Fact]
    public void Fixture_IsNotPartOfSrc()
    {
        SolutionAssemblies.Src.ShouldNotContain(FixtureAssembly);
        RepositoryLayout.SrcProjects().ShouldNotContain(p => p.Name == FixtureProjectName);
    }
}
