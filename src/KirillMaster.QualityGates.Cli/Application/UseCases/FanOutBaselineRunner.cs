using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Gates;

namespace KirillMaster.QualityGates.Cli.Application.UseCases;

public sealed record FanOutBaselineResult(string Path, int Added, int Lowered, int Removed);

/// <summary>Records the transitive fan-out of the projects over the limit in the baseline fanOut section. Shrink-only: a recorded value never rises, drops to the current value when lower, and the entry is removed once the project is back within the limit (or gone).</summary>
public sealed class FanOutBaselineRunner(IBaselineStore baselines, IPrFiles files, Func<string, string>? readText = null)
{
    private static readonly string[] ExcludedSegments = ["/obj/", "/bin/", "/node_modules/"];

    public FanOutBaselineResult Update(string repo, int limit)
    {
        var path = Path.Combine(repo, "quality", "baseline.json");
        var existing = baselines.Load(path)
                       ?? throw new InvalidOperationException($"no baseline found at {path}; run a PR build to bootstrap it first");

        var read = readText ?? (path => File.ReadAllText(path));
        var csprojs = files.Expand([Path.Combine(repo, "**", "*.csproj")])
            .Where(f => !ExcludedSegments.Any(s => f.Replace('\\', '/').Contains(s, StringComparison.Ordinal)))
            .ToDictionary(f => f, read, StringComparer.Ordinal);
        var graph = ProjectGraph.Build(csprojs);

        var before = existing.FanOut ?? new Dictionary<string, int>();
        var after = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int added = 0, lowered = 0, removed = 0;
        foreach (var project in graph.Keys)
        {
            var current = ProjectGraph.Transitive(graph, project).Count;
            if (current <= limit)
            {
                continue;
            }

            if (before.TryGetValue(project, out var recorded))
            {
                after[project] = Math.Min(recorded, current);
                lowered += current < recorded ? 1 : 0;
            }
            else
            {
                after[project] = current;
                added++;
            }
        }

        removed = before.Keys.Count(k => !after.ContainsKey(k));
        if (added + lowered + removed > 0)
        {
            baselines.Save(path, existing with { FanOut = after.Count == 0 ? null : after });
        }

        return new FanOutBaselineResult(path, added, lowered, removed);
    }
}
