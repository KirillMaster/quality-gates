using System.Globalization;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

public sealed record CouplingOptions(int Commits = 500, int MinSupport = 5, int MinFileCommits = 8, double MinConfidence = 0.8, int MaxCommitFiles = 50, bool BlockNewClusters = false, int MinClusterPartners = 2);

/// <summary>Hint (warn): a changed .cs file A usually changes together with a file B that this PR leaves alone. Confidence is co-changes of A and B over all commits touching A in the history window; mass commits are ignored. With <see cref="CouplingOptions.BlockNewClusters"/> a changed file with at least <see cref="CouplingOptions.MinClusterPartners"/> partners whose coupling is new (absent in the older half of the window) fails (C10). Adapter-boundary files (Entities, Mappers, Ports.Output) are never partners of each other (CN-7).</summary>
public static class ChangeCouplingGate
{
    public static GateResult Evaluate(IReadOnlyCollection<string> changed, IReadOnlyList<IReadOnlyList<string>> history, Func<string, bool> exists, CouplingOptions options)
    {
        var commits = history.Where(c => c.Count > 0 && c.Count <= options.MaxCommitFiles).Select(c => c.Where(IsSource).Distinct(StringComparer.Ordinal).ToList()).ToList();
        if (commits.Count == 0)
        {
            return new GateResult(GateId.ChangeCoupling, Verdict.NotApplicable, Message: "no usable git history on the target branch");
        }

        var touched = changed.Where(IsSource).ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        var clusters = new List<string>();
        IReadOnlyList<IReadOnlyList<string>> older = [.. commits.Skip(commits.Count / 2)];
        foreach (var file in touched.Order(StringComparer.Ordinal))
        {
            var withFile = commits.Where(c => c.Contains(file, StringComparer.Ordinal)).ToList();
            if (withFile.Count < options.MinFileCommits)
            {
                continue;
            }

            var newPartners = 0;
            var partners = withFile.SelectMany(c => c).Where(f => f != file && !touched.Contains(f)).GroupBy(f => f, StringComparer.Ordinal);
            foreach (var partner in partners.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var confidence = partner.Count() / (double)withFile.Count;
                if (partner.Count() >= options.MinSupport && confidence >= options.MinConfidence && exists(partner.Key) && !(IsBoundary(file) && IsBoundary(partner.Key)))
                {
                    newPartners += IsNewCoupling(file, partner.Key, older, options) ? 1 : 0;
                    offenders.Add($"{file} usually changes with {partner.Key} ({(confidence * 100).ToString("0", CultureInfo.InvariantCulture)}%, n={withFile.Count.ToString(CultureInfo.InvariantCulture)})");
                }
            }

            if (options.BlockNewClusters && newPartners >= options.MinClusterPartners)
            {
                clusters.Add($"{file} forms a new coupling cluster with {newPartners.ToString(CultureInfo.InvariantCulture)} files");
            }
        }

        if (clusters.Count > 0)
        {
            return new GateResult(GateId.ChangeCoupling, Verdict.Fail, Actual: clusters.Count, Threshold: 0, Offenders: [.. clusters, .. offenders], Message: $"{clusters.Count.ToString(CultureInfo.InvariantCulture)} new coupling cluster(s) not covered by this PR; change the coupled files or break the coupling");
        }

        return offenders.Count == 0
            ? new GateResult(GateId.ChangeCoupling, Verdict.Pass, Actual: 0, Threshold: 0, Message: $"no strong co-change partners missing from the PR ({commits.Count.ToString(CultureInfo.InvariantCulture)} commit(s) analysed)")
            : new GateResult(GateId.ChangeCoupling, Verdict.Warn, Actual: offenders.Count, Threshold: 0, Offenders: offenders, Message: $"{offenders.Count} strongly coupled file(s) not changed in this PR (hint only)");
    }

    private static bool IsNewCoupling(string file, string partner, IReadOnlyList<IReadOnlyList<string>> older, CouplingOptions options)
    {
        var withFile = older.Count(c => c.Contains(file, StringComparer.Ordinal));
        var together = older.Count(c => c.Contains(file, StringComparer.Ordinal) && c.Contains(partner, StringComparer.Ordinal));
        return withFile < options.MinFileCommits / 2 || together < options.MinSupport / 2 || together / (double)withFile < options.MinConfidence;
    }

    private static bool IsBoundary(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/Entities/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/Mappers/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Ports.Output", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/Ports/Output/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSource(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !path.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase);
}
