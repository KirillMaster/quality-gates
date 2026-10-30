using System.Globalization;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

public sealed record RatchetOutcome(GateResult Static, GateResult Coverage, Baseline? InitialBaseline)
{
    public IReadOnlyList<GateResult> Results => [Static, Coverage];
}

public static class BaselineRatchet
{
    public const decimal AllowedCoverageDropPp = 0.1m;

    private const string Bootstrap = "no baseline found: bootstrap mode, initial baseline produced";

    public static RatchetOutcome Evaluate(Baseline? baseline, Baseline current)
    {
        if (baseline is null)
        {
            return new RatchetOutcome(
                new GateResult(GateId.StaticRatchet, Verdict.Warn, Message: Bootstrap),
                new GateResult(GateId.CoverageRatchet, Verdict.Warn, Message: Bootstrap),
                current);
        }

        return new RatchetOutcome(EvaluateStatic(baseline, current), EvaluateCoverage(baseline, current), null);
    }

    private static GateResult EvaluateStatic(Baseline baseline, Baseline current)
    {
        var offenders = new List<string>();
        var largest = 0;
        CollectIncreases(baseline.Warnings, current.Warnings, string.Empty, offenders, ref largest);
        CollectIncreases(baseline.Grandfathered, current.Grandfathered, " (grandfathered)", offenders, ref largest);
        offenders.Sort(StringComparer.Ordinal);

        return offenders.Count == 0
            ? new GateResult(GateId.StaticRatchet, Verdict.Pass)
            : new GateResult(GateId.StaticRatchet, Verdict.Fail, Actual: largest, Threshold: 0, Offenders: offenders);
    }

    private static void CollectIncreases(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? baseline,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? current,
        string label,
        List<string> offenders,
        ref int largest)
    {
        if (current is null)
        {
            return;
        }

        foreach (var project in current)
        {
            IReadOnlyDictionary<string, int>? before = null;
            baseline?.TryGetValue(project.Key, out before);

            foreach (var rule in project.Value)
            {
                var previous = 0;
                before?.TryGetValue(rule.Key, out previous);
                var delta = rule.Value - previous;
                if (delta > 0)
                {
                    offenders.Add($"{project.Key}: {rule.Key}{label} +{delta.ToString(CultureInfo.InvariantCulture)}");
                    if (delta > largest)
                    {
                        largest = delta;
                    }
                }
            }
        }
    }

    private static GateResult EvaluateCoverage(Baseline baseline, Baseline current)
    {
        var offenders = new List<string>();
        var largest = 0m;

        foreach (var project in current.Projects)
        {
            if (project.Layer is Layer.Test or Layer.Excluded)
            {
                continue;
            }

            var before = 0m;
            foreach (var candidate in baseline.Projects)
            {
                if (candidate.Name == project.Name)
                {
                    before = (decimal)candidate.LineCoverage;
                    break;
                }
            }

            var now = (decimal)project.LineCoverage;
            var drop = before - now;
            if (drop > AllowedCoverageDropPp)
            {
                offenders.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{project.Name}: line coverage {now} < baseline {before} (-{drop} pp)"));
                if (drop > largest)
                {
                    largest = drop;
                }
            }
        }

        offenders.Sort(StringComparer.Ordinal);

        return offenders.Count == 0
            ? new GateResult(GateId.CoverageRatchet, Verdict.Pass)
            : new GateResult(
                GateId.CoverageRatchet,
                Verdict.Fail,
                Actual: (double)largest,
                Threshold: (double)AllowedCoverageDropPp,
                Offenders: offenders);
    }
}
