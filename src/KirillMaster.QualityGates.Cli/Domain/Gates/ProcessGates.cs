using System.Globalization;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

public enum ChangeKind { Behavioral, Bugfix, Structural }

/// <summary>Path conventions shared by the process gates. A test file sits under a folder named like a test project (<see cref="LayerClassifier.IsTestName" />) or under a <c>tests</c> folder.</summary>
public static class SourcePaths
{
    public static bool IsCSharp(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public static bool IsTest(string path)
    {
        var folders = path.Replace('\\', '/').Split('/')[..^1];
        return folders.Any(f => f.Equals("tests", StringComparison.OrdinalIgnoreCase) || LayerClassifier.IsTestName(f));
    }

    public static bool IsProduction(string path) =>
        IsCSharp(path) && !IsTest(path)
        && !path.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith(".GlobalSuppressions.cs", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Z2 / CN-2: a behavioral or bugfix change of production code must change tests too. A structural change is judged by the coverage ratchet (coverage of moved lines must not drop), not by a test diff.</summary>
public static class ChangedCodeChangedTestsGate
{
    public static GateResult Evaluate(IReadOnlyCollection<FileChange> changes, ChangeKind kind)
    {
        if (kind == ChangeKind.Structural)
        {
            return new GateResult(GateId.ChangedCodeChangedTests, Verdict.NotApplicable, Message: "structural change: no test diff required, the coverage ratchet guards moved lines");
        }

        var production = changes.Where(c => SourcePaths.IsProduction(c.Path)).Select(c => c.Path).Order(StringComparer.Ordinal).ToList();
        if (production.Count == 0)
        {
            return new GateResult(GateId.ChangedCodeChangedTests, Verdict.NotApplicable, Message: "no production C# changed");
        }

        var tests = changes.Count(c => SourcePaths.IsCSharp(c.Path) && SourcePaths.IsTest(c.Path));
        return tests > 0
            ? new GateResult(GateId.ChangedCodeChangedTests, Verdict.Pass, Actual: tests, Threshold: 1, Message: $"{tests.ToString(CultureInfo.InvariantCulture)} test file(s) changed with {production.Count.ToString(CultureInfo.InvariantCulture)} production file(s)")
            : new GateResult(GateId.ChangedCodeChangedTests, Verdict.Fail, Actual: 0, Threshold: 1, Offenders: production, Message: $"{kind.ToString().ToLowerInvariant()} change touches production code but no test file; add or change a test, or mark the PR structural");
    }
}

/// <summary>G2 (C32): a feature folder added by the PR needs a README.md that says what the feature is.</summary>
public static class FeatureReadmeGate
{
    public const int MinLength = 40;

    public static GateResult Evaluate(IReadOnlyCollection<string> newFeatureDirs, Func<string, string?> readmeOf)
    {
        if (newFeatureDirs.Count == 0)
        {
            return new GateResult(GateId.FeatureReadme, Verdict.NotApplicable, Message: "no new feature folder");
        }

        var offenders = new List<string>();
        foreach (var dir in newFeatureDirs.Order(StringComparer.Ordinal))
        {
            var text = readmeOf(dir);
            if (text is null)
            {
                offenders.Add($"{dir}: README.md is missing");
            }
            else if (text.Trim().Length < MinLength)
            {
                offenders.Add($"{dir}: README.md has fewer than {MinLength.ToString(CultureInfo.InvariantCulture)} characters");
            }
        }

        return offenders.Count == 0
            ? new GateResult(GateId.FeatureReadme, Verdict.Pass, Actual: 0, Threshold: 0, Message: $"{newFeatureDirs.Count.ToString(CultureInfo.InvariantCulture)} new feature folder(s) documented")
            : new GateResult(GateId.FeatureReadme, Verdict.Fail, Actual: offenders.Count, Threshold: 0, Offenders: offenders, Message: $"{offenders.Count.ToString(CultureInfo.InvariantCulture)} new feature folder(s) without a README");
    }
}

/// <summary>Z20 (report): churn of the changed files over the history window and instability (Ce / (Ca + Ce)) of the changed projects. Never fails.</summary>
public static class ProcessReportsGate
{
    public const int TopChurn = 5;

    public static GateResult Evaluate(IReadOnlyCollection<string> changedFiles, IReadOnlyList<IReadOnlyList<string>> history, IReadOnlyDictionary<string, IReadOnlySet<string>> graph, IReadOnlyCollection<string> changedProjects)
    {
        var lines = new List<string>();
        var churn = changedFiles.Where(SourcePaths.IsCSharp).Distinct(StringComparer.Ordinal)
            .Select(f => (File: f, Commits: history.Count(c => c.Contains(f, StringComparer.Ordinal))))
            .Where(r => r.Commits > 0)
            .OrderByDescending(r => r.Commits).ThenBy(r => r.File, StringComparer.Ordinal)
            .Take(TopChurn);
        lines.AddRange(churn.Select(r => $"churn: {r.File} changed in {r.Commits.ToString(CultureInfo.InvariantCulture)} of the last {history.Count.ToString(CultureInfo.InvariantCulture)} commit(s)"));

        foreach (var project in changedProjects.Where(graph.ContainsKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var efferent = graph[project].Count;
            var afferent = graph.Count(g => g.Value.Contains(project));
            var instability = efferent + afferent == 0 ? 0 : efferent / (double)(efferent + afferent);
            lines.Add($"instability: {project} I={instability.ToString("0.00", CultureInfo.InvariantCulture)} (Ce={efferent.ToString(CultureInfo.InvariantCulture)}, Ca={afferent.ToString(CultureInfo.InvariantCulture)})");
        }

        return lines.Count == 0
            ? new GateResult(GateId.ProcessReports, Verdict.NotApplicable, Message: "no churn or project metrics for the changed files")
            : new GateResult(GateId.ProcessReports, Verdict.Pass, Actual: lines.Count, Offenders: lines, Message: "churn and instability of changed code (report only)");
    }
}

public sealed record EndpointFlow(string Endpoint, IReadOnlyList<string> Calls);

/// <summary>E2 / G3: generic Mermaid text diagrams, no rendering dependency.</summary>
public static class MermaidDiagrams
{
    public static string ProjectEdges(IReadOnlyCollection<string> added, IReadOnlyCollection<string> removed)
    {
        var text = new System.Text.StringBuilder("flowchart LR\n");
        foreach (var edge in added.Order(StringComparer.Ordinal))
        {
            AppendEdge(text, edge, "-->");
        }

        foreach (var edge in removed.Order(StringComparer.Ordinal))
        {
            AppendEdge(text, edge, "-.->");
        }

        return text.ToString();
    }

    public static string RequestFlows(IReadOnlyCollection<EndpointFlow> flows)
    {
        var text = new System.Text.StringBuilder("flowchart LR\n");
        foreach (var flow in flows.OrderBy(f => f.Endpoint, StringComparer.Ordinal))
        {
            text.Append("  ").Append(Id(flow.Endpoint)).Append("[\"").Append(flow.Endpoint).Append("\"]\n");
            foreach (var call in flow.Calls.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                text.Append("  ").Append(Id(flow.Endpoint)).Append(" --> ").Append(Id(call)).Append("[\"").Append(call).Append("\"]\n");
            }
        }

        return text.ToString();
    }

    private static void AppendEdge(System.Text.StringBuilder text, string edge, string arrow)
    {
        var parts = edge.Split("->", 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            text.Append("  ").Append(Id(parts[0])).Append("[\"").Append(parts[0]).Append("\"] ").Append(arrow).Append(' ').Append(Id(parts[1])).Append("[\"").Append(parts[1]).Append("\"]\n");
        }
    }

    private static string Id(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}

/// <summary>Y14 (report): the Stryker mutation score of a nightly run against the committed score; a drop is a warning, a better score is reported so the baseline can be moved up.</summary>
public static class MutationRatchetGate
{
    public static double Score(IReadOnlyCollection<MutantResult> mutants)
    {
        var detected = mutants.Count(m => m.IsDetected);
        var total = detected + mutants.Count(m => m.IsUndetected);
        return total == 0 ? 100 : Math.Round(detected * 100.0 / total, 2);
    }

    public static GateResult Evaluate(IReadOnlyCollection<MutantResult> mutants, double? baselineScore)
    {
        if (mutants.Count(m => m.IsDetected || m.IsUndetected) == 0)
        {
            return new GateResult(GateId.MutationRatchet, Verdict.NotApplicable, Message: "no testable mutants in the report");
        }

        var score = Score(mutants);
        var survivors = mutants.Where(m => m.IsUndetected).GroupBy(m => m.File, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(10)
            .Select(g => $"{g.Key}: {g.Count().ToString(CultureInfo.InvariantCulture)} surviving mutant(s)").ToList();
        if (baselineScore is not { } baseline)
        {
            return new GateResult(GateId.MutationRatchet, Verdict.Pass, Actual: score, Offenders: survivors, Message: $"no mutation baseline yet; score {score.ToString("0.##", CultureInfo.InvariantCulture)}% becomes the baseline");
        }

        return score < baseline
            ? new GateResult(GateId.MutationRatchet, Verdict.Warn, Actual: score, Threshold: baseline, Offenders: survivors, Message: $"mutation score {score.ToString("0.##", CultureInfo.InvariantCulture)}% is below the baseline {baseline.ToString("0.##", CultureInfo.InvariantCulture)}%")
            : new GateResult(GateId.MutationRatchet, Verdict.Pass, Actual: score, Threshold: baseline, Offenders: survivors, Message: $"mutation score {score.ToString("0.##", CultureInfo.InvariantCulture)}% (baseline {baseline.ToString("0.##", CultureInfo.InvariantCulture)}%)");
    }
}
