using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

public sealed record BaselineChange(Baseline Baseline, int Entries);

public static class BaselineEvolution
{
    /// <summary>
    /// Lowers baseline warning counts to the current ones for projects that were measured; null when nothing got lower.
    /// Commit, MeasuredAt and coverage stay as they were: only the warning counts are re-measured, and lowering them never needs a new stamp.
    /// </summary>
    public static BaselineChange? Tighten(Baseline previous, Baseline current)
    {
        var lowered = 0;
        var warnings = new SortedDictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);

        foreach (var project in previous.Warnings)
        {
            var rules = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var measured = current.Warnings.TryGetValue(project.Key, out var now);
            foreach (var rule in project.Value)
            {
                var actual = rule.Value;
                if (measured)
                {
                    var seen = now!.GetValueOrDefault(rule.Key);
                    if (seen < actual)
                    {
                        actual = seen;
                        lowered++;
                    }
                }

                if (actual > 0)
                {
                    rules[rule.Key] = actual;
                }
            }

            warnings[project.Key] = rules;
        }

        return lowered == 0
            ? null
            : new BaselineChange(previous with { Warnings = warnings }, lowered);
    }

    /// <summary>Adds counts only for (project, rule) pairs absent from the baseline; existing values stay untouched.</summary>
    public static BaselineChange AddMissing(
        Baseline existing,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> found)
    {
        var added = 0;
        var warnings = new SortedDictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var project in existing.Warnings)
        {
            warnings[project.Key] = new SortedDictionary<string, int>(new Dictionary<string, int>(project.Value), StringComparer.Ordinal);
        }

        foreach (var project in found)
        {
            var rules = warnings.TryGetValue(project.Key, out var known)
                ? (SortedDictionary<string, int>)known
                : new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var rule in project.Value)
            {
                if (rules.TryAdd(rule.Key, rule.Value))
                {
                    added++;
                }
            }

            warnings[project.Key] = rules;
        }

        return new BaselineChange(existing with { Warnings = warnings }, added);
    }
}
