using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain;

/// <summary>
/// One matrix row. A pattern ending with "/" matches on a whole-segment prefix; any other pattern matches the whole name.
/// </summary>
public sealed record BranchRule(string SourcePattern, string TargetPattern, Policy Policy);

public static class BranchMatrix
{
    private const string RemotePrefix = "origin/";

    public static IReadOnlyList<BranchRule> DefaultRules { get; } =
    [
        new("feature/", "develop", Policy.Blocking),
        new("bugfix/", "develop", Policy.Blocking),
        new("hotfix/", "master", Policy.Skip),
        new("develop", "release/", Policy.Skip),
        new("release/", "master", Policy.Skip),
    ];

    public static Policy Resolve(string? source, string? target, IReadOnlyList<BranchRule>? rules = null)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            return Policy.ReportOnly;
        }

        var s = Normalize(source);
        var t = Normalize(target);

        foreach (var rule in rules ?? DefaultRules)
        {
            if (Matches(rule.SourcePattern, s) && Matches(rule.TargetPattern, t))
            {
                return rule.Policy;
            }
        }

        return Policy.ReportOnly;
    }

    private static string Normalize(string branch)
    {
        var b = branch.Trim();
        return b.StartsWith(RemotePrefix, StringComparison.Ordinal) ? b[RemotePrefix.Length..] : b;
    }

    private static bool Matches(string pattern, string branch) =>
        pattern.EndsWith('/')
            ? branch.Length > pattern.Length && branch.StartsWith(pattern, StringComparison.Ordinal)
            : string.Equals(pattern, branch, StringComparison.Ordinal);
}
