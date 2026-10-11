using Acme.Service.ArchitectureTests.Support;
using Shouldly;

namespace Acme.Service.ArchitectureTests;

/// <summary>R9 (INV-25): no forbidden or commercially licensed packages anywhere in the dependency graph.</summary>
public sealed class ForbiddenPackagesTests
{
    [Fact]
    public void R9_Repository_ReferencesNoForbiddenPackages()
    {
        var lockFiles = RepositoryLayout.LockFiles();
        lockFiles.ShouldNotBeEmpty();

        var result = ArchRules.NoForbiddenPackages(
            Path.Combine(RepositoryLayout.Root, "Directory.Packages.props"), RepositoryLayout.AllProjects(), lockFiles);

        result.Success.ShouldBeTrue(result.Message);
    }
}
