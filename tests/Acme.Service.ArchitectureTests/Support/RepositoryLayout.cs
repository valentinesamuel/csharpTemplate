using System.Xml.Linq;

namespace Acme.Service.ArchitectureTests.Support;

/// <summary>A project file and the names of the projects and packages it references.</summary>
/// <param name="Name">Project name (file name without extension).</param>
/// <param name="FullPath">Absolute path of the project file.</param>
/// <param name="ProjectReferences">Absolute paths of referenced project files.</param>
/// <param name="PackageReferences">Package ids referenced by the project.</param>
/// <param name="FrameworkReferences">Framework reference names.</param>
internal sealed record ProjectFile(
    string Name,
    string FullPath,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> FrameworkReferences)
{
    public IEnumerable<string> ReferencedProjectNames => ProjectReferences.Select(Path.GetFileNameWithoutExtension)!;

    public static ProjectFile Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var document = XDocument.Load(fullPath);

        IEnumerable<string> Includes(string itemName) => document
            .Descendants()
            .Where(e => e.Name.LocalName == itemName)
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!.Trim());

        var projectReferences = Includes("ProjectReference")
            .Select(include => Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();

        return new ProjectFile(
            Path.GetFileNameWithoutExtension(fullPath),
            fullPath,
            projectReferences,
            Includes("PackageReference").ToList(),
            Includes("FrameworkReference").ToList());
    }
}

/// <summary>Locates the repository root and its project files without hard-coded absolute paths.</summary>
internal static class RepositoryLayout
{
    /// <summary>Solution file that marks the repository root.</summary>
    public const string SolutionFileName = "Acme.Service.slnx";

    private static readonly Lazy<string> _lazyRoot = new(FindRoot);

    /// <summary>Absolute path of the repository root (the directory holding the solution file).</summary>
    public static string Root => _lazyRoot.Value;

    public static string SrcDirectory => Path.Combine(Root, "src");

    public static string TestsDirectory => Path.Combine(Root, "tests");

    /// <summary>Every project under <c>src/</c>.</summary>
    public static IReadOnlyList<ProjectFile> SrcProjects() => ProjectsUnder(SrcDirectory);

    /// <summary>Every project under <c>src/</c> and <c>tests/</c>.</summary>
    public static IReadOnlyList<ProjectFile> AllProjects() => [.. ProjectsUnder(SrcDirectory), .. ProjectsUnder(TestsDirectory)];

    /// <summary>Every <c>packages.lock.json</c> under <c>src/</c> and <c>tests/</c>.</summary>
    public static IReadOnlyList<string> LockFiles() =>
        [.. FilesUnder(SrcDirectory, "packages.lock.json"), .. FilesUnder(TestsDirectory, "packages.lock.json")];

    public static bool IsUnder(string path, string directory)
    {
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedDirectory, StringComparison.Ordinal);
    }

    private static List<ProjectFile> ProjectsUnder(string directory) =>
        FilesUnder(directory, "*.csproj").Select(ProjectFile.Load).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

    private static IEnumerable<string> FilesUnder(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static bool IsBuildOutput(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj");
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not find {SolutionFileName} in '{AppContext.BaseDirectory}' or any parent directory.");
    }
}
