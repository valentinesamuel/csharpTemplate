using System.Text;
using Acme.Service.ArchitectureTests.Support;
using Shouldly;

namespace Acme.Service.ArchitectureTests;

/// <summary>
/// INV-23 (plan §0.1, P23): the template's vendor name occurs only inside the three template tokens, so that
/// scripts/init.cs replaces every occurrence. After init the tokens below are replaced too, and the same scan then
/// proves that no occurrence is left at all.
/// </summary>
public sealed class TemplateTokenTests
{
    private const string PascalToken = "Acme.Service";
    private const string KebabToken = "acme-service";
    private const string SnakeToken = "acme_service";
    private const string LockFileName = "packages.lock.json";

    /// <summary>NuGet lower-cases project names in lock files; scripts/init.cs replaces this form as well.</summary>
    private static readonly string _lockFileToken = PascalToken.ToLowerInvariant();

    /// <summary>The searched word, assembled so this file does not contain it outside the tokens.</summary>
    private static readonly string _vendor = string.Concat("ac", "me");

    /// <summary>Generated, local-only or ignored directories (by name) that are not part of the template.</summary>
    private static readonly HashSet<string> _skippedDirectoryNames = new(StringComparer.Ordinal)
    {
        ".git", "bin", "obj", ".vs", ".idea", ".vscode", "TestResults", "node_modules", ".data",
    };

    [Fact]
    public void TextFiles_AfterRemovingTemplateTokens_ContainNoOtherOccurrence()
    {
        var files = TemplateFiles().ToList();
        files.ShouldNotBeEmpty();

        var violations = new List<string>();
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                continue; // binary
            }

            var relativePath = Path.GetRelativePath(RepositoryLayout.Root, file);
            violations.AddRange(FindViolations(relativePath, Encoding.Latin1.GetString(bytes)));
        }

        violations.ShouldBeEmpty(
            $"Only the exact template tokens may contain '{_vendor}' (any case); scripts/init.cs replaces nothing else:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void PathNames_AfterRemovingTemplateTokens_ContainNoOtherOccurrence()
    {
        var offending = TemplateFiles()
            .Select(file => Path.GetRelativePath(RepositoryLayout.Root, file))
            .Where(relativePath => StripTokens(relativePath, allowLockFileToken: false)
                .Contains(_vendor, StringComparison.OrdinalIgnoreCase))
            .ToList();

        offending.ShouldBeEmpty(string.Join(Environment.NewLine, offending));
    }

    [Fact]
    public void FindViolations_TextWithStrayOccurrence_ReportsLine()
    {
        var stray = _vendor.ToUpperInvariant() + "_X";
        var text = string.Join('\n', $"namespace {PascalToken}.Host;", $"image: {KebabToken}", $"var x = \"{stray}\";");

        var violations = FindViolations("sample.txt", text);

        violations.ShouldHaveSingleItem().ShouldStartWith("sample.txt:3:");
    }

    [Fact]
    public void FindViolations_LowercaseDottedForm_AllowedOnlyInLockFiles()
    {
        // Built from parts so both assertions also hold after init has replaced the tokens in this file.
        var lockFileText = $"\"{_lockFileToken}.host\": {{ \"type\": \"Project\" }}";
        var otherText = $"\"{_vendor}.service.host\": {{ \"type\": \"Project\" }}";

        FindViolations(Path.Combine("src", LockFileName), lockFileText).ShouldBeEmpty();
        FindViolations(Path.Combine("src", "appsettings.json"), otherText).ShouldHaveSingleItem();
    }

    private static List<string> FindViolations(string relativePath, string content)
    {
        var allowLockFileToken = string.Equals(Path.GetFileName(relativePath), LockFileName, StringComparison.Ordinal);
        var violations = new List<string>();
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (StripTokens(lines[i], allowLockFileToken).Contains(_vendor, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
            }
        }

        return violations;
    }

    private static string StripTokens(string text, bool allowLockFileToken)
    {
        var stripped = text
            .Replace(PascalToken, string.Empty, StringComparison.Ordinal)
            .Replace(KebabToken, string.Empty, StringComparison.Ordinal)
            .Replace(SnakeToken, string.Empty, StringComparison.Ordinal);
        return allowLockFileToken ? stripped.Replace(_lockFileToken, string.Empty, StringComparison.Ordinal) : stripped;
    }

    private static IEnumerable<string> TemplateFiles()
    {
        // .claude/artifacts holds the build plan and agent state; it is not part of the template and init deletes it (D-26).
        var claudeArtifacts = Path.Combine(RepositoryLayout.Root, ".claude", "artifacts");
        var pending = new Stack<string>();
        pending.Push(RepositoryLayout.Root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                yield return file;
            }

            foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            {
                if (!_skippedDirectoryNames.Contains(Path.GetFileName(subdirectory))
                    && !string.Equals(subdirectory, claudeArtifacts, StringComparison.Ordinal))
                {
                    pending.Push(subdirectory);
                }
            }
        }
    }
}
