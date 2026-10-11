using System.Text;

namespace Acme.Service.ArchitectureTests;

/// <summary>One rule violation: the offending type, project or package, and why it violates the rule.</summary>
/// <param name="Subject">Offending type full name, project name or package id.</param>
/// <param name="Detail">What it depends on or why it is not allowed.</param>
internal sealed record ArchViolation(string Subject, string Detail)
{
    public override string ToString() => $"{Subject}: {Detail}";
}

/// <summary>Outcome of an <see cref="ArchRules"/> rule run over a set of assemblies or project files.</summary>
internal sealed class ArchRuleResult
{
    public ArchRuleResult(string ruleId, string description, IEnumerable<ArchViolation> violations)
    {
        RuleId = ruleId;
        Description = description;
        Violations = violations
            .Distinct()
            .OrderBy(v => v.Subject, StringComparer.Ordinal)
            .ThenBy(v => v.Detail, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Rule id from plan P18 (R1 to R10) or the invariant id.</summary>
    public string RuleId { get; }

    public string Description { get; }

    public IReadOnlyList<ArchViolation> Violations { get; }

    public bool Success => Violations.Count == 0;

    /// <summary>Distinct offending type, project or package names.</summary>
    public IReadOnlyList<string> OffendingNames =>
        Violations.Select(v => v.Subject).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Human-readable summary naming the rule and every violation; used as the assertion message.</summary>
    public string Message
    {
        get
        {
            if (Success)
            {
                return $"{RuleId} passed: {Description}";
            }

            var builder = new StringBuilder()
                .Append(RuleId).Append(" violated (").Append(Description).Append("): ")
                .Append(Violations.Count).Append(" violation(s)");
            foreach (var violation in Violations)
            {
                builder.AppendLine().Append(" - ").Append(violation);
            }

            return builder.ToString();
        }
    }

    public override string ToString() => Message;
}
