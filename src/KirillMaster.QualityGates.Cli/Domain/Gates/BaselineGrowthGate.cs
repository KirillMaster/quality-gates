using System.Globalization;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

/// <summary>One baseline group (a JSON baseline file, or all GlobalSuppressions files together) before and after the PR.</summary>
public sealed record BaselineDelta(string Group, IReadOnlyDictionary<string, int> Base, IReadOnlyDictionary<string, int> Head);

/// <summary>Baselines may only shrink: a larger count or a new key fails. A new key is allowed only while keys removed in the same group carry at least as much (a rename or move, CN-3). A deleted baseline file, or one renamed outside the baseline globs, counts as growth (the ratchet would restart from nothing). Growth passes with a warning when a code owner other than the PR author approved it (AM-8); approvers come from the CI (an APPROVED GitHub review), never from the PR content.</summary>
public static class BaselineGrowthGate
{
    public static GateResult Evaluate(IReadOnlyCollection<BaselineDelta> deltas, IReadOnlyCollection<string> approvers, IReadOnlySet<string> owners, string? author = null, IReadOnlyCollection<string>? removedBaselines = null)
    {
        var growth = new List<string>();
        foreach (var delta in deltas.OrderBy(d => d.Group, StringComparer.Ordinal))
        {
            growth.AddRange(Growth(delta));
        }

        growth.AddRange((removedBaselines ?? []).Order(StringComparer.Ordinal).Select(path => $"{path}: baseline removed"));

        if (growth.Count == 0)
        {
            return new GateResult(GateId.BaselineGrowth, Verdict.Pass, Message: $"no baseline growth ({deltas.Count} baseline group(s) changed)");
        }

        var authorKnown = !string.IsNullOrWhiteSpace(author);
        var independent = authorKnown ? approvers.Where(a => !a.Equals(author, StringComparison.OrdinalIgnoreCase)).ToList() : [];
        var approvedBy = independent.FirstOrDefault(owners.Contains);
        if (approvedBy is not null)
        {
            return new GateResult(GateId.BaselineGrowth, Verdict.Warn, Actual: growth.Count, Threshold: 0, Offenders: growth, Message: $"{growth.Count} baseline growth(s) approved by code owner {approvedBy}");
        }

        var note = approvers.Count == 0
            ? "needs an APPROVED review by a code owner, passed with --growth-approved-by"
            : !authorKnown
                ? "--pr-author is required to accept an approver"
                : independent.Count == 0
                    ? "the PR author cannot approve their own baseline growth"
                    : $"approver(s) {string.Join(", ", independent)} not listed in CODEOWNERS";
        return new GateResult(GateId.BaselineGrowth, Verdict.Fail, Actual: growth.Count, Threshold: 0, Offenders: growth, Message: $"{growth.Count} baseline growth(s); {note}");
    }

    private static List<string> Growth(BaselineDelta delta)
    {
        var offenders = new List<string>();
        var newKeys = new List<(string Key, int Count)>();
        foreach (var (key, count) in delta.Head.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!delta.Base.TryGetValue(key, out var before))
            {
                newKeys.Add((key, count));
            }
            else if (count > before)
            {
                offenders.Add($"{delta.Group}: {key} {before.ToString(CultureInfo.InvariantCulture)} -> {count.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        var removed = delta.Base.Where(k => !delta.Head.ContainsKey(k.Key)).Sum(k => k.Value);
        if (newKeys.Sum(k => k.Count) > removed)
        {
            offenders.AddRange(newKeys.Select(k => $"{delta.Group}: {k.Key} new ({k.Count.ToString(CultureInfo.InvariantCulture)})"));
        }

        return offenders;
    }
}
