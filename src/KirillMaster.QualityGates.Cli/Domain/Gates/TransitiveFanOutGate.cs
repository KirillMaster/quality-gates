using System.Globalization;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

/// <summary>C08 (report): transitive project fan-out of every changed project; a project above the limit is a warning in the report, never a failure. A project recorded in the baseline fanOut section is grandfathered: it warns only above its recorded value (shrink-only), every other project against the limit.</summary>
public static class TransitiveFanOutGate
{
    public const int DefaultLimit = 25;

    public static GateResult Evaluate(IReadOnlyDictionary<string, IReadOnlySet<string>> graph, IReadOnlyCollection<string> changedProjects, int limit = DefaultLimit, IReadOnlyDictionary<string, int>? baseline = null)
    {
        var rows = changedProjects.Where(graph.ContainsKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(p => (Project: p, Count: ProjectGraph.Transitive(graph, p).Count))
            .ToList();
        if (rows.Count == 0)
        {
            return new GateResult(GateId.TransitiveFanOut, Verdict.NotApplicable, Message: "no changed project in the reference graph");
        }

        int AllowedFor(string project) => baseline is not null && baseline.TryGetValue(project, out var recorded) ? recorded : limit;
        string Marker((string Project, int Count) row) => baseline is not null && baseline.ContainsKey(row.Project) ? " (over baseline)" : " (over limit)";

        var offenders = rows.OrderByDescending(r => r.Count).ThenBy(r => r.Project, StringComparer.Ordinal)
            .Select(r => $"{r.Project}: {r.Count.ToString(CultureInfo.InvariantCulture)} transitive reference(s){(r.Count > AllowedFor(r.Project) ? Marker(r) : string.Empty)}")
            .ToList();
        var message = $"{rows.Count.ToString(CultureInfo.InvariantCulture)} changed project(s), limit {limit.ToString(CultureInfo.InvariantCulture)} transitive references";
        return new GateResult(GateId.TransitiveFanOut, rows.Any(r => r.Count > AllowedFor(r.Project)) ? Verdict.Warn : Verdict.Pass, Actual: rows.Max(r => r.Count), Threshold: limit, Offenders: offenders, Message: message);
    }
}
