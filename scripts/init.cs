#:property PublishAot=false
#:property RestorePackagesWithLockFile=false
// Template initialization (plan P23, D-16, D-26, INV-23). Usage, from the repository root of a FRESH clone:
//
//   dotnet run scripts/init.cs -- --name Contoso.Billing [--remove-sample] [--dry-run] [--force]
//
// What it does, in order (nothing is written until every step has been planned and checked):
//   1. Refuses (exit 1) if the git working tree is dirty (unless --force) or if the template solution file is absent
//      (the repository is already initialized).
//   2. Validates --name against ^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)+$ (exit 2) and derives the other forms.
//   3. --remove-sample: deletes the Orders projects and their tests and strips every sample:orders region (the
//      opening and closing marker lines included) from all text files. Markers are whole comment lines in the forms
//      '//', '#' or '<!-- -->' around the tag; a file left empty by the stripping is deleted.
//   4. Replaces the template tokens in every text file (skipping .git/, bin/, obj/, .claude/artifacts/ and binary
//      files, i.e. files containing a NUL byte). Files are processed byte-for-byte (Latin-1 round trip), so UTF-8
//      content, a BOM if present, line endings and file modes are preserved exactly.
//   5. Renames directories and files whose names contain a token, deepest first (two-step rename via a temporary
//      name when only the letter case changes, for case-insensitive file systems).
//   6. Deletes .claude/artifacts/ and .claude/settings.local.json (D-26) and the bin/ and obj/ build output next to
//      every project file (it carries the old project names).
//   7. Prints the next steps (restore with --force-evaluate, because the lock files name the projects, and
//      'dotnet format', because using directives sort differently under the new root namespace).
//      With --dry-run it prints the planned changes and writes nothing.
//
// Name forms, for --name Contoso.BillingApi:
//   PascalCase dotted  Contoso.BillingApi   (solution, projects, assemblies, namespaces, folders)
//   kebab-case         contoso-billing-api  (image, compose project, k8s names/labels, OTel service.name, JWT defaults)
//   snake_case         contoso_billing_api  (Postgres database name)
//   lowercase dotted   contoso.billingapi   (project names inside NuGet-generated packages.lock.json files)
// kebab/snake rule: split the name on '.', split each segment again before every upper-case letter that follows a
// lower-case letter or a digit, lower-case every part, and join with '-' (kebab) or '_' (snake).
//
// Exit codes: 0 success, 1 refusal (dirty tree, already initialized, conflicting state), 2 usage or validation error.
// The token strings are assembled from parts so that this file contains none of them (INV-23, TemplateTokenTests).
// No packages are used; the directives above stop the file-based app from writing a platform-specific lock file.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

const string VendorLower = "ac" + "me";
const string PascalToken = "Ac" + "me.Service";
const string KebabToken = VendorLower + "-service";
const string SnakeToken = VendorLower + "_service";
const string LockToken = VendorLower + ".service";
const string SolutionFileName = PascalToken + ".slnx";
const string SampleTag = "sample:orders";

string? name = null;
var removeSample = false;
var dryRun = false;
var force = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--name" when i + 1 < args.Length:
            name = args[++i];
            break;
        case var arg when arg.StartsWith("--name=", StringComparison.Ordinal):
            name = arg["--name=".Length..];
            break;
        case "--remove-sample":
            removeSample = true;
            break;
        case "--dry-run":
            dryRun = true;
            break;
        case "--force":
            force = true;
            break;
        case "-h" or "--help":
            PrintUsage(Console.Out);
            return 0;
        default:
            Console.Error.WriteLine($"Unknown or incomplete argument '{args[i]}'.");
            PrintUsage(Console.Error);
            return 2;
    }
}

if (string.IsNullOrWhiteSpace(name))
{
    Console.Error.WriteLine("--name is required.");
    PrintUsage(Console.Error);
    return 2;
}

var nameRegex = new Regex(@"^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
if (!nameRegex.IsMatch(name))
{
    Console.Error.WriteLine($"Invalid --name '{name}'. Use PascalCase dotted segments, e.g. Contoso.Billing (regex {nameRegex}).");
    return 2;
}

if (name.Contains(VendorLower, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Invalid --name '{name}': it collides with the template tokens. Choose your own organization and service name.");
    return 2;
}

var kebab = DeriveSeparated(name, '-');
var snake = DeriveSeparated(name, '_');
var lockName = name.ToLowerInvariant();
(string Token, string Value)[] replacements =
[
    (PascalToken, name),
    (KebabToken, kebab),
    (SnakeToken, snake),
    (LockToken, lockName),
];

var root = FindRepositoryRoot(Directory.GetCurrentDirectory());
if (root is null)
{
    Console.Error.WriteLine($"{SolutionFileName} was not found in the current directory or any parent: the repository is already initialized, or this is not the template. Nothing was changed.");
    return 1;
}

var gitProblem = CheckGitClean(root);
if (gitProblem is not null)
{
    if (force)
    {
        Console.WriteLine($"warning: {gitProblem} Continuing because of --force.");
    }
    else if (dryRun)
    {
        Console.WriteLine($"warning: {gitProblem} A real run will refuse without --force.");
    }
    else
    {
        Console.Error.WriteLine($"Refusing to run: {gitProblem} Commit or stash first, run on a fresh clone, or pass --force.");
        return 1;
    }
}

Console.WriteLine($"{(dryRun ? "Planning (dry run)" : "Initializing")} '{root}'");
Console.WriteLine($"  {PascalToken,-16} -> {name}");
Console.WriteLine($"  {KebabToken,-16} -> {kebab}");
Console.WriteLine($"  {SnakeToken,-16} -> {snake}");
Console.WriteLine($"  {LockToken,-16} -> {lockName}   (packages.lock.json)");
Console.WriteLine($"  remove sample: {(removeSample ? "yes" : "no")}");
Console.WriteLine();

// ---- Plan -------------------------------------------------------------------------------------------------------
var errors = new List<string>();
var claudeArtifacts = Path.Combine(root, ".claude", "artifacts");
var directoriesToDelete = new List<string>();
if (removeSample)
{
    directoriesToDelete.Add(Path.Combine(root, "src", "Modules", "Orders"));
    directoriesToDelete.Add(Path.Combine(root, "tests", PascalToken + ".Modules.Orders.Tests"));
}

if (Directory.Exists(claudeArtifacts))
{
    directoriesToDelete.Add(claudeArtifacts);
}

var excludedRoots = directoriesToDelete.ToList();
var allFiles = EnumerateFiles(root, excludedRoots).ToList();

// Build output beside every project carries the old names; it is regenerated by the next build.
foreach (var project in allFiles.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
{
    var projectDirectory = Path.GetDirectoryName(project)!;
    foreach (var output in new[] { "bin", "obj" })
    {
        var candidate = Path.Combine(projectDirectory, output);
        if (Directory.Exists(candidate))
        {
            directoriesToDelete.Add(candidate);
        }
    }
}

var filesToDelete = new List<string>();
var settingsLocal = Path.Combine(root, ".claude", "settings.local.json");
if (File.Exists(settingsLocal))
{
    filesToDelete.Add(settingsLocal);
}

var openMarker = MarkerRegex(SampleTag);
var closeMarker = MarkerRegex("/" + SampleTag);
var edits = new List<(string Path, string Text, int Replaced, int Stripped)>();
var binarySkipped = 0;
foreach (var file in allFiles)
{
    if (filesToDelete.Contains(file))
    {
        continue;
    }

    var bytes = File.ReadAllBytes(file);
    if (Array.IndexOf(bytes, (byte)0) >= 0)
    {
        binarySkipped++;
        continue;
    }

    // Latin-1 maps every byte to one char and back, so untouched bytes (UTF-8 sequences, BOM, CR/LF) survive exactly;
    // all tokens and markers are ASCII and cannot match inside a multi-byte UTF-8 sequence.
    var original = Encoding.Latin1.GetString(bytes);
    var text = original;
    var stripped = 0;
    if (removeSample)
    {
        (text, stripped) = StripSampleRegions(text, Path.GetRelativePath(root, file), openMarker, closeMarker, errors);
        if (stripped > 0 && string.IsNullOrWhiteSpace(text))
        {
            filesToDelete.Add(file);
            continue;
        }
    }

    var replaced = 0;
    foreach (var (token, value) in replacements)
    {
        replaced += CountOccurrences(text, token);
        text = text.Replace(token, value, StringComparison.Ordinal);
    }

    if (!string.Equals(text, original, StringComparison.Ordinal))
    {
        edits.Add((file, text, replaced, stripped));
    }
}

var renames = new List<(string From, string To)>();
foreach (var path in EnumerateEntries(root, excludedRoots).OrderByDescending(Depth).ThenBy(p => p, StringComparer.Ordinal))
{
    if (filesToDelete.Contains(path) || directoriesToDelete.Any(d => IsSameOrUnder(path, d)))
    {
        continue;
    }

    var entryName = Path.GetFileName(path);
    var newName = entryName;
    foreach (var (token, value) in replacements)
    {
        newName = newName.Replace(token, value, StringComparison.Ordinal);
    }

    if (string.Equals(newName, entryName, StringComparison.Ordinal))
    {
        continue;
    }

    var target = Path.Combine(Path.GetDirectoryName(path)!, newName);
    var caseOnly = string.Equals(newName, entryName, StringComparison.OrdinalIgnoreCase);
    if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
    {
        errors.Add($"Cannot rename '{Path.GetRelativePath(root, path)}': '{newName}' already exists.");
        continue;
    }

    renames.Add((path, target));
}

if (errors.Count > 0)
{
    foreach (var error in errors)
    {
        Console.Error.WriteLine($"error: {error}");
    }

    Console.Error.WriteLine("Nothing was changed.");
    return 1;
}

// ---- Report -----------------------------------------------------------------------------------------------------
string Rel(string path) => Path.GetRelativePath(root, path);

foreach (var directory in directoriesToDelete)
{
    Console.WriteLine($"delete dir   {Rel(directory)}/");
}

foreach (var file in filesToDelete)
{
    Console.WriteLine($"delete file  {Rel(file)}");
}

foreach (var (path, _, replaced, stripped) in edits)
{
    var details = new List<string>();
    if (replaced > 0)
    {
        details.Add($"{replaced} token(s)");
    }

    if (stripped > 0)
    {
        details.Add($"{stripped} sample line(s) removed");
    }

    Console.WriteLine($"edit         {Rel(path)} ({string.Join(", ", details)})");
}

foreach (var (from, to) in renames)
{
    Console.WriteLine($"rename       {Rel(from)} -> {Path.GetFileName(to)}");
}

Console.WriteLine();
Console.WriteLine($"{directoriesToDelete.Count} directories and {filesToDelete.Count} files to delete, {edits.Count} files to edit "
    + $"({edits.Sum(e => e.Replaced)} token replacements, {edits.Sum(e => e.Stripped)} sample lines), {renames.Count} renames; "
    + $"{binarySkipped} binary files skipped.");

if (dryRun)
{
    Console.WriteLine("Dry run: nothing was written.");
    return 0;
}

// ---- Apply ------------------------------------------------------------------------------------------------------
foreach (var directory in directoriesToDelete.Where(Directory.Exists))
{
    Directory.Delete(directory, recursive: true);
}

foreach (var file in filesToDelete.Where(File.Exists))
{
    File.Delete(file);
}

foreach (var (path, text, _, _) in edits)
{
    // Overwriting an existing file keeps its permissions (e.g. executable scripts).
    File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text));
}

foreach (var (from, to) in renames)
{
    var isDirectory = Directory.Exists(from);
    if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
    {
        var temporary = from + ".init-" + Guid.NewGuid().ToString("N")[..8];
        Move(from, temporary, isDirectory);
        Move(temporary, to, isDirectory);
    }
    else
    {
        Move(from, to, isDirectory);
    }
}

Console.WriteLine();
Console.WriteLine($"Done. The template is now {name}. Next steps:");
Console.WriteLine();
Console.WriteLine($"  dotnet restore {name}.slnx --force-evaluate     # project names changed, so the lock files are regenerated");
Console.WriteLine($"  dotnet format {name}.slnx                         # re-sorts using directives (the root namespace changed)");
Console.WriteLine($"  dotnet build {name}.slnx -c Release");
Console.WriteLine($"  dotnet test {name}.slnx --filter \"Category!=Integration\"");
Console.WriteLine("  dotnet run scripts/dev-env.cs                       # local secrets for docker compose and user-secrets");
Console.WriteLine($"  git add -A && git commit -m \"Initialize {name} from the template\"");
Console.WriteLine();
Console.WriteLine($"Also set your container registry in deploy/k8s/**/kustomization.yaml (newName: registry.example.com/your-org/{kebab}).");
return 0;

static void PrintUsage(TextWriter writer) =>
    writer.WriteLine("Usage: dotnet run scripts/init.cs -- --name <Org.Service> [--remove-sample] [--dry-run] [--force]");

static string DeriveSeparated(string dottedName, char separator)
{
    var builder = new StringBuilder();
    foreach (var segment in dottedName.Split('.'))
    {
        if (builder.Length > 0)
        {
            builder.Append(separator);
        }

        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (i > 0 && char.IsAsciiLetterUpper(c) && (char.IsAsciiLetterLower(segment[i - 1]) || char.IsAsciiDigit(segment[i - 1])))
            {
                builder.Append(separator);
            }

            builder.Append(char.ToLowerInvariant(c));
        }
    }

    return builder.ToString();
}

static string? FindRepositoryRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
        {
            return directory.FullName;
        }
    }

    return null;
}

static string? CheckGitClean(string root)
{
    var startInfo = new ProcessStartInfo("git")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in new[] { "-C", root, "--no-optional-locks", "status", "--porcelain" })
    {
        startInfo.ArgumentList.Add(argument);
    }

    try
    {
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            return "The working tree state cannot be verified (not a git repository).";
        }

        return output.Length == 0 ? null : "The git working tree has uncommitted changes.";
    }
    catch (Win32Exception)
    {
        return "The working tree state cannot be verified (git is not installed).";
    }
}

static bool IsSkippedDirectory(string path, IReadOnlyList<string> excludedRoots)
{
    var directoryName = Path.GetFileName(path);
    if (directoryName is ".git" or "bin" or "obj")
    {
        return true;
    }

    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
    {
        return true;
    }

    return excludedRoots.Any(excluded => IsSameOrUnder(path, excluded));
}

static IEnumerable<string> EnumerateFiles(string directory, IReadOnlyList<string> excludedRoots)
{
    foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
        {
            yield return file;
        }
    }

    foreach (var subdirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
    {
        if (IsSkippedDirectory(subdirectory, excludedRoots))
        {
            continue;
        }

        foreach (var file in EnumerateFiles(subdirectory, excludedRoots))
        {
            yield return file;
        }
    }
}

static IEnumerable<string> EnumerateEntries(string directory, IReadOnlyList<string> excludedRoots)
{
    foreach (var file in Directory.EnumerateFiles(directory))
    {
        yield return file;
    }

    foreach (var subdirectory in Directory.EnumerateDirectories(directory))
    {
        if (IsSkippedDirectory(subdirectory, excludedRoots))
        {
            continue;
        }

        yield return subdirectory;
        foreach (var entry in EnumerateEntries(subdirectory, excludedRoots))
        {
            yield return entry;
        }
    }
}

static int Depth(string path) => path.Count(c => c == Path.DirectorySeparatorChar);

static bool IsSameOrUnder(string path, string directory)
{
    var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
    return string.Equals(normalizedPath, normalizedDirectory, StringComparison.Ordinal)
        || normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

static int CountOccurrences(string text, string token)
{
    var count = 0;
    for (var index = text.IndexOf(token, StringComparison.Ordinal); index >= 0; index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal))
    {
        count++;
    }

    return count;
}

// A marker is a whole line: optional indentation, a comment opener ('//', '#' or '<!--'), the tag in angle brackets
// and, for XML/Markdown, the closing '-->'. Prose that merely mentions the tag never matches.
static Regex MarkerRegex(string tag) => new(
    @"^[ \t]*(?://|#|<!--)[ \t]*<" + Regex.Escape(tag) + @">[ \t]*(?:-->)?[ \t]*$",
    RegexOptions.CultureInvariant,
    TimeSpan.FromSeconds(1));

static (string Text, int Removed) StripSampleRegions(string text, string relativePath, Regex open, Regex close, List<string> errors)
{
    var output = new StringBuilder(text.Length);
    var removed = 0;
    var openedAt = 0;
    var lineNumber = 0;
    var start = 0;
    while (start < text.Length)
    {
        var newline = text.IndexOf('\n', start);
        var end = newline < 0 ? text.Length : newline + 1;
        var line = text[start..end];
        var content = line.TrimEnd('\n').TrimEnd('\r');
        lineNumber++;

        if (open.IsMatch(content))
        {
            if (openedAt != 0)
            {
                errors.Add($"{relativePath}:{lineNumber}: nested sample marker (region opened at line {openedAt}).");
            }

            openedAt = lineNumber;
            removed++;
        }
        else if (close.IsMatch(content))
        {
            if (openedAt == 0)
            {
                errors.Add($"{relativePath}:{lineNumber}: closing sample marker without an opening one.");
            }

            openedAt = 0;
            removed++;
        }
        else if (openedAt != 0)
        {
            removed++;
        }
        else
        {
            output.Append(line);
        }

        start = end;
    }

    if (openedAt != 0)
    {
        errors.Add($"{relativePath}:{openedAt}: sample marker region is never closed.");
    }

    return (output.ToString(), removed);
}

static void Move(string from, string to, bool isDirectory)
{
    if (isDirectory)
    {
        Directory.Move(from, to);
    }
    else
    {
        File.Move(from, to);
    }
}
