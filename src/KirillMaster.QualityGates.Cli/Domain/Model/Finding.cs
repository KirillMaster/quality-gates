namespace KirillMaster.QualityGates.Cli.Domain.Model;

/// <summary>An unsuppressed warning-or-higher analyzer result; <paramref name="Uri"/> is the raw SARIF artifact location.</summary>
public sealed record Finding(string Project, string RuleId, string Uri, int Line);

public sealed record DebtReport(
    IReadOnlyList<Finding> Findings,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? Baseline,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Current);
