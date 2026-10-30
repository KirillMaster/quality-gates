namespace KirillMaster.QualityGates.Cli.Domain.Model;

public sealed record ProjectMetrics(
    string Name,
    Layer Layer,
    double LineCoverage,
    double? BranchCoverage = null,
    double? MutationScore = null);

public sealed record Baseline(
    string Commit,
    string MeasuredAt,
    IReadOnlyList<ProjectMetrics> Projects,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Warnings,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? Grandfathered = null,
    IReadOnlyDictionary<string, int>? FanOut = null);
