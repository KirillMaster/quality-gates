using System.Globalization;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

public sealed record FileChange(string Path, string? OldPath, int Added, int Deleted);

public sealed record PrSizeLimits(int MaxLines = 400, int MaxFiles = 20);

/// <summary>Warns when a PR is too big to review (C26). Files marked linguist-generated, lockfiles, baselines and EF migration designers do not count.</summary>
public static class PrSizeGate
{
    private static readonly string[] LockFiles = ["package-lock.json", "yarn.lock", "pnpm-lock.yaml", "packages.lock.json", "poetry.lock", "Cargo.lock", "Gemfile.lock", "composer.lock"];

    public static bool IsExcluded(string path, IReadOnlyCollection<string> generatedPatterns)
    {
        var name = System.IO.Path.GetFileName(path);
        var normalized = path.Replace('\\', '/');
        return LockFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || name.Contains("baseline", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".GlobalSuppressions.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) && normalized.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)
            || PathGlob.IsMatchAny(generatedPatterns, path);
    }

    public static GateResult Evaluate(IReadOnlyCollection<FileChange> changes, IReadOnlyCollection<string> generatedPatterns, PrSizeLimits limits)
    {
        var counted = changes.Where(c => !IsExcluded(c.Path, generatedPatterns)).ToList();
        var lines = counted.Sum(c => c.Added + c.Deleted);
        var excluded = changes.Count - counted.Count;
        var summary = $"{lines.ToString(CultureInfo.InvariantCulture)} changed line(s) in {counted.Count.ToString(CultureInfo.InvariantCulture)} file(s), {excluded.ToString(CultureInfo.InvariantCulture)} generated/lock/baseline file(s) excluded; limit {limits.MaxLines.ToString(CultureInfo.InvariantCulture)} lines / {limits.MaxFiles.ToString(CultureInfo.InvariantCulture)} files";
        if (lines <= limits.MaxLines && counted.Count <= limits.MaxFiles)
        {
            return new GateResult(GateId.PrSize, Verdict.Pass, Actual: lines, Threshold: limits.MaxLines, Message: summary);
        }

        var offenders = counted.OrderByDescending(c => c.Added + c.Deleted).ThenBy(c => c.Path, StringComparer.Ordinal).Take(10)
            .Select(c => $"{c.Path} (+{c.Added.ToString(CultureInfo.InvariantCulture)}/-{c.Deleted.ToString(CultureInfo.InvariantCulture)})").ToList();
        return new GateResult(GateId.PrSize, Verdict.Warn, Actual: lines, Threshold: limits.MaxLines, Offenders: offenders, Message: "PR is large: " + summary + "; consider splitting it");
    }
}
