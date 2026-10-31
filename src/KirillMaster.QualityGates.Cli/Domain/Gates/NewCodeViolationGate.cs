using System.Globalization;
using KirillMaster.QualityGates.Cli.Domain.Diff;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

public static class NewCodeViolationGate
{
    public static GateResult Evaluate(ChangedLines changed, IEnumerable<Finding> findings, string repoRoot)
    {
        var root = Normalize(repoRoot).TrimEnd('/');
        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var finding in findings)
        {
            if (finding.Line <= 0 || Resolve(finding.Uri, root, changed) is not { } path || !changed.LinesOf(path).Contains(finding.Line))
            {
                continue;
            }

            offenders.Add(string.Create(CultureInfo.InvariantCulture, $"{path}:{finding.Line} {finding.RuleId}"));
        }

        return offenders.Count == 0
            ? new GateResult(GateId.NewCodeViolations, Verdict.Pass)
            : new GateResult(GateId.NewCodeViolations, Verdict.Fail, Actual: offenders.Count, Threshold: 0, Offenders: [.. offenders]);
    }

    /// <summary>
    /// Maps a SARIF uri to the changed repo-relative path: strips the repo root, else matches a changed path by suffix (container paths differ from the checkout).
    /// The suffix match needs a directory component and takes the longest candidate, so a root-level Program.cs does not claim every Program.cs in the build.
    /// </summary>
    private static string? Resolve(string uri, string root, ChangedLines changed)
    {
        var path = Normalize(uri);
        if (root.Length > 0 && path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return path[(root.Length + 1)..];
        }

        if (changed.Files.FirstOrDefault(file => string.Equals(path, file, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return exact;
        }

        string? longest = null;
        foreach (var file in changed.Files)
        {
            if (file.Contains('/', StringComparison.Ordinal) && path.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase)
                && file.Length > (longest?.Length ?? 0))
            {
                longest = file;
            }
        }

        return longest;
    }

    private static string Normalize(string value)
    {
        var path = value.Trim();
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            path = path["file://".Length..];
        }

        path = Uri.UnescapeDataString(path).Replace('\\', '/');
        return path.Length > 2 && path[0] == '/' && path[2] == ':' ? path[1..] : path;
    }
}
