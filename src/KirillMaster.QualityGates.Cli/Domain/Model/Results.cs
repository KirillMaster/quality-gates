namespace KirillMaster.QualityGates.Cli.Domain.Model;

public sealed record GateResult(
    GateId Gate,
    Verdict Verdict,
    ResultLayer? Layer = null,
    double? Actual = null,
    double? Threshold = null,
    IReadOnlyList<string>? Offenders = null,
    string? Message = null);

public sealed record RunReport(
    RunMode Mode,
    Policy Policy,
    IReadOnlyList<GateResult> Results,
    int ExitCode,
    string? SourceBranch = null,
    string? TargetBranch = null,
    string? BypassedBy = null,
    IReadOnlyList<string>? SkippedGroups = null,
    string? Error = null);

public sealed record HotspotEntry(
    string Path,
    double Churn,
    double Complexity,
    double Coverage,
    double Duplication,
    double Warnings,
    double Risk,
    double? MutationScore = null);
