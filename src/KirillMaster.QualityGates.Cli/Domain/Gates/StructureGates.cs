using System.Globalization;
using KirillMaster.QualityGates.Cli.Domain.Coverage;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

/// <summary>Facts read from the metadata of one compiled assembly: type counts, referenced assemblies and namespace to namespace edges inside the analysed set (<c>Asm:Ns -> Asm:Ns</c>).</summary>
public sealed record AssemblyFacts(string Name, int Types, int AbstractTypes, IReadOnlySet<string> References, IReadOnlySet<string> NamespaceEdges);

/// <summary>E2 (report): assembly and namespace edges from compiled assemblies at HEAD against assemblies built at the merge base. Without base assemblies only the totals are reported.</summary>
public static class ArchDeltaIlGate
{
    public static GateResult Evaluate(IReadOnlyList<AssemblyFacts> head, IReadOnlyList<AssemblyFacts>? baseline)
    {
        if (head.Count == 0)
        {
            return new GateResult(GateId.ArchDeltaIl, Verdict.NotApplicable, Message: "no assemblies given (--assemblies)");
        }

        var after = Edges(head);
        if (baseline is null || baseline.Count == 0)
        {
            return new GateResult(GateId.ArchDeltaIl, Verdict.Pass, Actual: after.Count, Message: $"{after.Count.ToString(CultureInfo.InvariantCulture)} assembly and namespace edge(s) at HEAD; no base assemblies (--assemblies-base), delta not computed");
        }

        var before = Edges(baseline);
        var offenders = after.Except(before).Order(StringComparer.Ordinal).Select(e => "+ " + e)
            .Concat(before.Except(after).Order(StringComparer.Ordinal).Select(e => "- " + e)).ToList();
        return new GateResult(GateId.ArchDeltaIl, Verdict.Pass, Actual: offenders.Count, Offenders: offenders,
            Message: offenders.Count == 0 ? "no assembly or namespace edge added or removed" : $"{offenders.Count.ToString(CultureInfo.InvariantCulture)} compiled edge change(s) (report only)");
    }

    public static HashSet<string> Edges(IReadOnlyList<AssemblyFacts> facts)
    {
        var names = facts.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var edges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            edges.UnionWith(fact.References.Where(names.Contains).Select(r => $"{fact.Name} -> {r}"));
            edges.UnionWith(fact.NamespaceEdges);
        }

        return edges;
    }
}

/// <summary>Z20 (report): distance from the main sequence D = |A + I - 1| per assembly, A = abstract types / types, I = Ce / (Ca + Ce) over the analysed set.</summary>
public static class MainSequenceGate
{
    public static GateResult Evaluate(IReadOnlyList<AssemblyFacts> facts)
    {
        if (facts.Count == 0)
        {
            return new GateResult(GateId.MainSequence, Verdict.NotApplicable, Message: "no assemblies given (--assemblies)");
        }

        var names = facts.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var rows = new List<(string Name, double A, double I, double D)>();
        foreach (var fact in facts.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var ce = fact.References.Count(names.Contains);
            var ca = facts.Count(o => o.References.Contains(fact.Name));
            var instability = ce + ca == 0 ? 0 : ce / (double)(ce + ca);
            var abstractness = fact.Types == 0 ? 0 : fact.AbstractTypes / (double)fact.Types;
            rows.Add((fact.Name, abstractness, instability, Math.Abs(abstractness + instability - 1)));
        }

        var offenders = rows.OrderByDescending(r => r.D).ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => $"{r.Name}: D={F(r.D)} (A={F(r.A)}, I={F(r.I)})").ToList();
        return new GateResult(GateId.MainSequence, Verdict.Pass, Actual: rows.Max(r => r.D), Offenders: offenders, Message: "distance from the main sequence per assembly (report only)");
    }

    private static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>Z20 (warn report): age of the oldest commit of the branch that is not on the target.</summary>
public static class BranchAgeGate
{
    public const int DefaultMaxDays = 7;

    public static GateResult Evaluate(DateTimeOffset? firstCommit, DateTimeOffset now, int maxDays = DefaultMaxDays)
    {
        if (firstCommit is not { } first)
        {
            return new GateResult(GateId.BranchAge, Verdict.NotApplicable, Message: "no branch commits found");
        }

        var days = Math.Max(0, (now - first).TotalDays);
        var text = days.ToString("0.0", CultureInfo.InvariantCulture);
        return days > maxDays
            ? new GateResult(GateId.BranchAge, Verdict.Warn, Actual: days, Threshold: maxDays, Message: $"branch is {text} day(s) old, limit {maxDays.ToString(CultureInfo.InvariantCulture)}; merge or split it")
            : new GateResult(GateId.BranchAge, Verdict.Pass, Actual: days, Threshold: maxDays, Message: $"branch is {text} day(s) old");
    }
}

public sealed record BuildRecord(int Number, string Result, long DurationMs);

/// <summary>Z20 (report): success ratio and duration trend of the last builds. Input is the parsed CI build history <c>api/json?tree=builds[number,result,duration]</c> document; without it the gate is not applicable.</summary>
public static class BuildTrendGate
{
    public static GateResult Evaluate(IReadOnlyList<BuildRecord>? builds)
    {
        if (builds is null || builds.Count == 0)
        {
            return new GateResult(GateId.BuildTrend, Verdict.NotApplicable, Message: "no build history given (--build-history a CI build history JSON)");
        }

        var finished = builds.OrderBy(b => b.Number).ToList();
        var green = finished.Count(b => string.Equals(b.Result, "SUCCESS", StringComparison.OrdinalIgnoreCase));
        var half = Math.Max(1, finished.Count / 2);
        var older = finished.Take(half).Average(b => b.DurationMs) / 1000.0;
        var recent = finished.Skip(finished.Count - half).Average(b => b.DurationMs) / 1000.0;
        var lines = new List<string>
        {
            $"green: {green.ToString(CultureInfo.InvariantCulture)} of {finished.Count.ToString(CultureInfo.InvariantCulture)} builds",
            $"duration: {older.ToString("0", CultureInfo.InvariantCulture)}s (older half) -> {recent.ToString("0", CultureInfo.InvariantCulture)}s (recent half)"
        };
        return new GateResult(GateId.BuildTrend, Verdict.Pass, Actual: green * 100.0 / finished.Count, Offenders: lines, Message: "build trend (report only)");
    }
}

public sealed record MovedLine(string OldFile, int OldLine, string NewFile, int NewLine);

/// <summary>Finds lines that were removed in one place and added in another by the same diff (a move or an extraction), matched on trimmed text of at least 4 characters.</summary>
public static class MovedLines
{
    /// <summary>Lines of the diff that are moved text, keyed <c>path:line</c> on the new side.</summary>
    public static HashSet<string> NewSideKeys(string diff) =>
        Find(diff).Select(m => Key(m.NewFile, m.NewLine)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string Key(string file, int line) => file + ":" + line.ToString(CultureInfo.InvariantCulture);

    public static IReadOnlyList<MovedLine> Find(string diff)
    {
        var removed = new Dictionary<string, Queue<(string File, int Line)>>(StringComparer.Ordinal);
        var added = new List<(string File, int Line, string Text)>();
        string? oldFile = null;
        string? newFile = null;
        var oldLine = 0;
        var newLine = 0;
        var inHunk = false;
        foreach (var line in diff.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.StartsWith("diff ", StringComparison.Ordinal))
            {
                inHunk = false;
            }
            else if (!inHunk && text.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldFile = text.Length > 6 && text.StartsWith("--- a/", StringComparison.Ordinal) ? text[6..] : null;
            }
            else if (!inHunk && text.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newFile = text.StartsWith("+++ b/", StringComparison.Ordinal) ? text[6..] : null;
            }
            else if (text.StartsWith("@@ ", StringComparison.Ordinal))
            {
                inHunk = true;
                var parts = text.Split(' ');
                oldLine = Start(parts[1]);
                newLine = Start(parts[2]);
            }
            else if (text.StartsWith('-') && oldFile is not null)
            {
                if (Meaningful(text[1..]))
                {
                    var key = text[1..].Trim();
                    if (!removed.TryGetValue(key, out var queue))
                    {
                        queue = new Queue<(string, int)>();
                        removed[key] = queue;
                    }

                    queue.Enqueue((oldFile, oldLine));
                }

                oldLine++;
            }
            else if (text.StartsWith('+') && newFile is not null)
            {
                if (Meaningful(text[1..]))
                {
                    added.Add((newFile, newLine, text[1..].Trim()));
                }

                newLine++;
            }
            else if (text.StartsWith(' '))
            {
                oldLine++;
                newLine++;
            }
        }

        var moved = new List<MovedLine>();
        foreach (var (file, lineNumber, text) in added)
        {
            if (removed.TryGetValue(text, out var queue) && queue.Count > 0)
            {
                var from = queue.Dequeue();
                moved.Add(new MovedLine(from.File, from.Line, file, lineNumber));
            }
        }

        return moved;
    }

    private static bool Meaningful(string text) => text.Trim().Length >= 4 && text.Trim() is not ("{" or "}" or "});" or "else");

    private static int Start(string range) => int.Parse(range[1..].Split(',')[0], CultureInfo.InvariantCulture);
}

/// <summary>Z2 / CN-2: for a structural change, a moved line that was covered in the base coverage must still be covered at HEAD. Needs Cobertura from the target branch; without it the gate is not applicable and the coverage ratchet is the only guard.</summary>
public static class MovedLineCoverageGate
{
    public static GateResult Evaluate(IReadOnlyList<MovedLine> moved, CoverageReport? baseCoverage, CoverageReport? headCoverage)
    {
        if (baseCoverage is null || headCoverage is null)
        {
            return new GateResult(GateId.MovedLineCoverage, Verdict.NotApplicable, Message: "no base or head coverage given (--base-coverage), the coverage ratchet guards moved lines");
        }

        var checkedLines = 0;
        var offenders = new List<string>();
        foreach (var line in moved.OrderBy(m => m.NewFile, StringComparer.Ordinal).ThenBy(m => m.NewLine))
        {
            if (!baseCoverage.TryGetHits(line.OldFile, line.OldLine, out var before) || before <= 0)
            {
                continue;
            }

            checkedLines++;
            if (!headCoverage.TryGetHits(line.NewFile, line.NewLine, out var after) || after <= 0)
            {
                offenders.Add($"{line.NewFile}:{line.NewLine.ToString(CultureInfo.InvariantCulture)} moved from {line.OldFile}:{line.OldLine.ToString(CultureInfo.InvariantCulture)} was covered, now is not");
            }
        }

        if (checkedLines == 0)
        {
            var csharp = moved.Count(m => SourcePaths.IsCSharp(m.OldFile));
            if (csharp > 0 && !moved.Any(m => SourcePaths.IsCSharp(m.OldFile) && baseCoverage.HasFile(m.OldFile)))
            {
                return new GateResult(GateId.MovedLineCoverage, Verdict.Warn, Actual: csharp, Message: $"none of {csharp.ToString(CultureInfo.InvariantCulture)} moved C# line(s) maps to the base coverage report: check --base-coverage-root, the guard is not active");
            }

            return new GateResult(GateId.MovedLineCoverage, Verdict.NotApplicable, Message: "no covered moved lines");
        }

        return offenders.Count == 0
            ? new GateResult(GateId.MovedLineCoverage, Verdict.Pass, Actual: 0, Threshold: 0, Message: $"{checkedLines.ToString(CultureInfo.InvariantCulture)} covered moved line(s) are still covered")
            : new GateResult(GateId.MovedLineCoverage, Verdict.Fail, Actual: offenders.Count, Threshold: 0, Offenders: offenders, Message: $"{offenders.Count.ToString(CultureInfo.InvariantCulture)} moved line(s) lost coverage");
    }
}
