using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Grandfather;

namespace KirillMaster.QualityGates.Cli.Application.UseCases;

public sealed record GrandfatherResult(int Sites, IReadOnlyList<string> Projects);

public sealed class GrandfatherRunner(IBannedCallScanner scanner, IGrandfatherFiles files, IBaselineStore baselines)
{
    public const string Rule = "RS0030";

    /// <summary>Scans <paramref name="repoRoot"/>; the suppression files and quality/baseline.json go under <paramref name="qualityRoot"/> (the directory the build resolves as QualityGatesRoot, repoRoot by default).</summary>
    public GrandfatherResult Run(string repoRoot, IReadOnlyList<string> sarifPaths, DateOnly date, string? qualityRoot = null)
    {
        qualityRoot ??= repoRoot;
        var byProject = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var site in scanner.Scan(repoRoot, sarifPaths))
        {
            if (!byProject.TryGetValue(site.Project, out var members))
            {
                members = new SortedSet<string>(StringComparer.Ordinal);
                byProject[site.Project] = members;
            }

            members.Add(site.MemberDocId);
        }

        var total = 0;
        var counts = new Dictionary<string, IReadOnlyDictionary<string, int>>();
        foreach (var (project, members) in byProject)
        {
            files.Write(qualityRoot, project, GrandfatherSuppressions.Render(members, date));
            counts[project] = new Dictionary<string, int> { [Rule] = members.Count };
            total += members.Count;
        }

        foreach (var existing in files.ExistingProjects(qualityRoot))
        {
            if (!byProject.ContainsKey(existing))
            {
                files.Delete(qualityRoot, existing);
            }
        }

        var baselinePath = Path.Combine(qualityRoot, "quality", "baseline.json");
        var baseline = baselines.Load(baselinePath);
        if (baseline is not null)
        {
            baselines.Save(baselinePath, baseline with { Grandfathered = counts });
        }

        return new GrandfatherResult(total, [.. byProject.Keys]);
    }
}
