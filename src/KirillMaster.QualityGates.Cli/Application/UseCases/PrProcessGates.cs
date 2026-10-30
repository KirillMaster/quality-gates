using System.Globalization;
using System.Text.Json;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Application.UseCases;

public sealed record PrProcessOptions(
    ChangeKind Kind = ChangeKind.Behavioral,
    IReadOnlyList<string>? FeatureRoots = null,
    ComplexityDeltaOptions? Complexity = null,
    int FanOutLimit = TransitiveFanOutGate.DefaultLimit,
    int MaxScannedFiles = 3000,
    int CouplingHistory = 500,
    IReadOnlyList<string>? Assemblies = null,
    IReadOnlyList<string>? BaseAssemblies = null,
    IReadOnlyList<string>? BaseCoverage = null,
    string? BaseCoverageCommit = null,
    string? BaseCoverageRoot = null,
    string? BuildHistory = null,
    int MaxBranchAgeDays = BranchAgeGate.DefaultMaxDays)
{
    public static readonly IReadOnlyList<string> DefaultFeatureRoots = ["**/Features/*"];

    /// <summary>Structural moved-line coverage needs the base Cobertura and the commit it was built at.</summary>
    public bool HasBaseCoverage => BaseCoverage is { Count: > 0 } && !string.IsNullOrWhiteSpace(BaseCoverageCommit);

    /// <summary>A structural change without base coverage is judged as behavioral (DiffCoverage and the test diff stay on): fail back to the strict kind.</summary>
    public ChangeKind EffectiveKind => Kind == ChangeKind.Structural && !HasBaseCoverage ? ChangeKind.Behavioral : Kind;
}

/// <summary>Optional inputs of the structure gates; a gate whose port is missing is not applicable.</summary>
public sealed record PrProcessPorts(IAssemblyReader? Assemblies = null, IBranchInfo? Branch = null, IPrFiles? Files = null, ICoverageReader? Coverage = null, IGitClient? Git = null);

/// <summary>The B8 CI gates computed from the pull-request delta and the HEAD tree: CognitiveComplexityDelta, FeatureReadme, ChangedCodeChangedTests and MovedLineCoverage (blocking); ToggleExpiry, DeadPublic, BranchAge (warn); TransitiveFanOut, ProcessReports, ArchFlows, ArchDeltaIl, MainSequence, BuildTrend (report only).</summary>
public sealed class PrProcessGates(IPrChangeSource source, ICodeInsights insights, IArtifactWriter artifacts, TimeProvider clock, PrProcessPorts? ports = null)
{
    private static readonly QualityGroup[] Groups =
    [
        QualityGroup.Complexity, QualityGroup.Toggles, QualityGroup.DeadPublic, QualityGroup.FanOut,
        QualityGroup.Reports, QualityGroup.FeatureReadme, QualityGroup.Flows, QualityGroup.TestDiff,
        QualityGroup.IlGraph, QualityGroup.BranchAge, QualityGroup.BuildTrend, QualityGroup.MovedCoverage
    ];

    public async Task<IReadOnlyList<GateResult>> EvaluateAsync(PrRequest request, CancellationToken cancellationToken = default)
    {
        var skipped = request.Skip ?? [];
        var options = request.Process ?? new PrProcessOptions();
        if (Groups.All(skipped.Contains))
        {
            return [.. Gates.Select(g => Bypassed(g))];
        }

        IReadOnlyList<FileChange> changes = [];
        string? changesError = null;
        try
        {
            changes = await source.ChangesAsync(request.Repo, request.Target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            changesError = ex.Message;
        }

        var context = new Context(request, options, changes, cancellationToken);
        var results = new List<GateResult>
        {
            await Blocking(GateId.CognitiveComplexityDelta, QualityGroup.Complexity, skipped, changesError, () => ComplexityAsync(context)).ConfigureAwait(false),
            await Blocking(GateId.FeatureReadme, QualityGroup.FeatureReadme, skipped, changesError, () => FeatureReadmeAsync(context)).ConfigureAwait(false),
            await Blocking(GateId.ChangedCodeChangedTests, QualityGroup.TestDiff, skipped, changesError, () => Task.FromResult(ChangedCodeChangedTestsGate.Evaluate(changes, options.EffectiveKind))).ConfigureAwait(false),
            await Advisory(GateId.ToggleExpiry, QualityGroup.Toggles, skipped, changesError, () => ToggleExpiryAsync(context)).ConfigureAwait(false),
            await Advisory(GateId.DeadPublic, QualityGroup.DeadPublic, skipped, changesError, () => DeadPublicAsync(context)).ConfigureAwait(false),
            await Advisory(GateId.TransitiveFanOut, QualityGroup.FanOut, skipped, changesError, () => FanOutAsync(context)).ConfigureAwait(false),
            await Advisory(GateId.ProcessReports, QualityGroup.Reports, skipped, changesError, () => ReportsAsync(context)).ConfigureAwait(false),
            await Advisory(GateId.ArchFlows, QualityGroup.Flows, skipped, changesError, () => FlowsAsync(context)).ConfigureAwait(false),
            await Advisory(GateId.ArchDeltaIl, QualityGroup.IlGraph, skipped, null, () => Task.FromResult(IlDelta(options))).ConfigureAwait(false),
            await Advisory(GateId.MainSequence, QualityGroup.IlGraph, skipped, null, () => Task.FromResult(MainSequence(options))).ConfigureAwait(false),
            await Advisory(GateId.BranchAge, QualityGroup.BranchAge, skipped, null, () => BranchAgeAsync(context)).ConfigureAwait(false),
            await Advisory(GateId.BuildTrend, QualityGroup.BuildTrend, skipped, null, () => Task.FromResult(BuildTrend(options))).ConfigureAwait(false),
            await Blocking(GateId.MovedLineCoverage, QualityGroup.MovedCoverage, skipped, null, () => MovedCoverageAsync(context)).ConfigureAwait(false)
        };
        return results;
    }

    private static GateId[] Gates =>
    [
        GateId.CognitiveComplexityDelta, GateId.FeatureReadme, GateId.ChangedCodeChangedTests, GateId.ToggleExpiry,
        GateId.DeadPublic, GateId.TransitiveFanOut, GateId.ProcessReports, GateId.ArchFlows,
        GateId.ArchDeltaIl, GateId.MainSequence, GateId.BranchAge, GateId.BuildTrend, GateId.MovedLineCoverage
    ];

    private GateResult IlDelta(PrProcessOptions options)
    {
        var head = ReadAssemblies(options.Assemblies);
        var baseline = options.BaseAssemblies is { Count: > 0 } ? ReadAssemblies(options.BaseAssemblies) : null;
        return ArchDeltaIlGate.Evaluate(head, baseline);
    }

    private GateResult MainSequence(PrProcessOptions options) => MainSequenceGate.Evaluate(ReadAssemblies(options.Assemblies));

    private IReadOnlyList<AssemblyFacts> ReadAssemblies(IReadOnlyList<string>? globs) =>
        ports?.Assemblies is { } reader && ports.Files is { } files && globs is { Count: > 0 } ? reader.Read(files.Expand(globs)) : [];

    private async Task<GateResult> BranchAgeAsync(Context context)
    {
        if (ports?.Branch is not { } branch)
        {
            return new GateResult(GateId.BranchAge, Verdict.NotApplicable, Message: "branch info is not available");
        }

        var first = await branch.FirstBranchCommitAsync(context.Request.Repo, context.Request.Target, context.Token).ConfigureAwait(false);
        return BranchAgeGate.Evaluate(first, clock.GetUtcNow(), context.Options.MaxBranchAgeDays);
    }

    private GateResult BuildTrend(PrProcessOptions options)
    {
        if (options.BuildHistory is not { Length: > 0 } path)
        {
            return BuildTrendGate.Evaluate(null);
        }

        var text = artifacts.Read(path);
        if (text is null)
        {
            return new GateResult(GateId.BuildTrend, Verdict.Warn, Message: $"build history file {path} not found");
        }

        try
        {
            return BuildTrendGate.Evaluate(ParseBuilds(text));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new GateResult(GateId.BuildTrend, Verdict.Warn, Message: $"build history {path} is not valid JSON: {ex.Message}");
        }
    }

    private static List<BuildRecord> ParseBuilds(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("builds", out var builds) || builds.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new System.Text.Json.JsonException("build history has no \"builds\" array");
        }

        var records = new List<BuildRecord>();
        foreach (var build in builds.EnumerateArray().Where(b => b.ValueKind == System.Text.Json.JsonValueKind.Object))
        {
            if (build.TryGetProperty("number", out var number) && number.TryGetInt32(out var n)
                && build.TryGetProperty("result", out var result) && result.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                records.Add(new BuildRecord(n, result.GetString()!, build.TryGetProperty("duration", out var duration) && duration.TryGetInt64(out var ms) ? ms : 0));
            }
        }

        return records;
    }

    private async Task<GateResult> MovedCoverageAsync(Context context)
    {
        if (context.Options.Kind != ChangeKind.Structural)
        {
            return new GateResult(GateId.MovedLineCoverage, Verdict.NotApplicable, Message: "only for structural changes");
        }

        if (!context.Options.HasBaseCoverage)
        {
            return new GateResult(GateId.MovedLineCoverage, Verdict.Warn, Message: "structural change declared without --base-coverage and --base-coverage-commit: treated as behavioral, DiffCoverage and the test diff apply");
        }

        if (ports?.Coverage is not { } coverage || ports.Files is not { } files || context.Options.BaseCoverage is not { Count: > 0 } baseGlobs)
        {
            return MovedLineCoverageGate.Evaluate([], null, null);
        }

        var baseFiles = files.Expand(baseGlobs);
        var headFiles = files.Expand(context.Request.CoverageGlobs);
        if (baseFiles.Count == 0 || headFiles.Count == 0)
        {
            throw new IOException($"moved-line coverage needs Cobertura files for base ({string.Join(", ", baseGlobs)}) and head ({string.Join(", ", context.Request.CoverageGlobs)})");
        }

        var diff = await source.DiffFromAsync(context.Request.Repo, context.Options.BaseCoverageCommit!, context.Token).ConfigureAwait(false);
        return MovedLineCoverageGate.Evaluate(MovedLines.Find(diff), coverage.ReadMergedFromOtherRoot(baseFiles, context.Request.Repo, context.Options.BaseCoverageRoot), coverage.ReadMerged(headFiles, context.Request.Repo));
    }

    private static async Task<GateResult> Advisory(GateId gate, QualityGroup group, IReadOnlyCollection<QualityGroup> skipped, string? changesError, Func<Task<GateResult>> run)
    {
        if (skipped.Contains(group))
        {
            return Bypassed(gate);
        }

        if (changesError is not null)
        {
            return new GateResult(gate, Verdict.Warn, Message: $"{gate} could not run: {changesError}");
        }

        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new GateResult(gate, Verdict.Warn, Message: $"{gate} could not run: {ex.Message}");
        }
    }

    private static async Task<GateResult> Blocking(GateId gate, QualityGroup group, IReadOnlyCollection<QualityGroup> skipped, string? changesError, Func<Task<GateResult>> run)
    {
        if (skipped.Contains(group))
        {
            return Bypassed(gate);
        }

        if (changesError is not null)
        {
            throw new PrInputException($"{gate} could not run: {changesError}");
        }

        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            throw new PrInputException($"{gate} could not run: {ex.Message}", ex);
        }
    }

    private async Task<GateResult> ComplexityAsync(Context context)
    {
        var pairs = new List<MethodComplexityChange>();
        foreach (var change in context.Changes.Where(c => SourcePaths.IsProduction(c.Path)))
        {
            var head = await ReadAsync(context, PrRevision.Head, change.Path).ConfigureAwait(false);
            if (head is null)
            {
                continue;
            }

            var before = await ReadAsync(context, PrRevision.Base, change.OldPath ?? change.Path).ConfigureAwait(false);
            pairs.Add(new MethodComplexityChange(change.Path, before is null ? new Dictionary<string, int>() : insights.CognitiveComplexity(before), insights.CognitiveComplexity(head)));
        }

        return CognitiveComplexityDeltaGate.Evaluate(pairs, context.Options.Complexity ?? new ComplexityDeltaOptions());
    }

    private async Task<GateResult> FeatureReadmeAsync(Context context)
    {
        var roots = context.Options.FeatureRoots is { Count: > 0 } custom ? custom : PrProcessOptions.DefaultFeatureRoots;
        var byDir = new SortedDictionary<string, List<FileChange>>(StringComparer.Ordinal);
        foreach (var change in context.Changes.Where(c => !c.Path.EndsWith("/README.md", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var dir in Directories(change.Path).Where(d => PathGlob.IsMatchAny(roots, d)))
            {
                if (!byDir.TryGetValue(dir, out var list))
                {
                    list = [];
                    byDir[dir] = list;
                }

                list.Add(change);
            }
        }

        var added = new List<string>();
        if (byDir.Count > 0)
        {
            var baseFiles = await source.BaseFilesAsync(context.Request.Repo, context.Request.Target, context.Token).ConfigureAwait(false);
            added.AddRange(byDir.Keys.Where(dir => !baseFiles.Any(f => f.StartsWith(dir + "/", StringComparison.Ordinal))));
        }

        var readmes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var dir in added)
        {
            readmes[dir] = await ReadAsync(context, PrRevision.Head, dir + "/README.md").ConfigureAwait(false);
        }

        return FeatureReadmeGate.Evaluate(added, d => readmes.GetValueOrDefault(d));
    }

    private async Task<GateResult> ToggleExpiryAsync(Context context)
    {
        var markers = new List<ToggleMarker>();
        var files = await SourceFilesAsync(context).ConfigureAwait(false);
        if (files is null)
        {
            return TooManyFiles(GateId.ToggleExpiry, context);
        }

        foreach (var (path, text) in files)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(ToggleExpiryGate.Pattern, StringComparison.Ordinal))
                {
                    markers.Add(new ToggleMarker(path, i + 1, lines[i]));
                }
            }
        }

        return ToggleExpiryGate.Evaluate(markers, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
    }

    private async Task<GateResult> DeadPublicAsync(Context context)
    {
        var files = await SourceFilesAsync(context).ConfigureAwait(false);
        if (files is null)
        {
            return TooManyFiles(GateId.DeadPublic, context);
        }

        var changed = context.Changes.Where(c => SourcePaths.IsProduction(c.Path)).Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var declared = new List<PublicType>();
        var identifiers = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var (path, text) in files)
        {
            identifiers[path] = insights.Identifiers(text);
            if (changed.Contains(path))
            {
                declared.AddRange(insights.PublicTypes(text).Where(IsReferencedByConvention).Select(n => new PublicType(path, n)));
            }
        }

        return DeadPublicGate.Evaluate(declared, identifiers);
    }

    private async Task<GateResult> FanOutAsync(Context context)
    {
        var (graph, projectDirs) = await GraphAsync(context).ConfigureAwait(false);
        var baseline = await FanOutBaselineAsync(context).ConfigureAwait(false);
        return TransitiveFanOutGate.Evaluate(graph, ChangedProjects(context, projectDirs), context.Options.FanOutLimit, baseline);
    }

    /// <summary>The grandfathered fan-out counts of the HEAD quality/baseline.json; growth of that section is BaselineGrowth's business.</summary>
    private async Task<IReadOnlyDictionary<string, int>?> FanOutBaselineAsync(Context context)
    {
        const string path = "quality/baseline.json";
        const string prefix = BaselineCounts.FanOutSection + "/";
        var texts = await source.ReadManyAsync(context.Request.Repo, context.Request.Target, PrRevision.Head, [path], context.Token).ConfigureAwait(false);
        var text = texts.GetValueOrDefault(path);
        try
        {
            return BaselineCounts.Parse(path, text).Where(c => c.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(c => c.Key[prefix.Length..], c => c.Value, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"malformed JSON in {path}: {ex.Message}", ex);
        }
    }

    private async Task<GateResult> ReportsAsync(Context context)
    {
        var (graph, projectDirs) = await GraphAsync(context).ConfigureAwait(false);
        var history = await source.HistoryAsync(context.Request.Repo, context.Request.Target, context.Options.CouplingHistory, context.Token).ConfigureAwait(false);
        return ProcessReportsGate.Evaluate(context.Changes.Select(c => c.Path).ToList(), history, graph, ChangedProjects(context, projectDirs));
    }

    private async Task<GateResult> FlowsAsync(Context context)
    {
        var added = new List<string>();
        var removed = new List<string>();
        foreach (var change in context.Changes.Where(c => c.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var before = ArchDeltaGate.Edges(change.OldPath ?? change.Path, await ReadAsync(context, PrRevision.Base, change.OldPath ?? change.Path).ConfigureAwait(false));
            var after = ArchDeltaGate.Edges(change.Path, await ReadAsync(context, PrRevision.Head, change.Path).ConfigureAwait(false));
            added.AddRange(after.Except(before));
            removed.AddRange(before.Except(after));
        }

        var flows = new List<EndpointFlow>();
        foreach (var change in context.Changes.Where(c => SourcePaths.IsProduction(c.Path)))
        {
            if (await ReadAsync(context, PrRevision.Head, change.Path).ConfigureAwait(false) is { } text)
            {
                flows.AddRange(insights.Flows(text));
            }
        }

        if (added.Count + removed.Count == 0 && flows.Count == 0)
        {
            return new GateResult(GateId.ArchFlows, Verdict.NotApplicable, Message: "no project edge or endpoint flow changed");
        }

        var written = new List<string>();
        if (added.Count + removed.Count > 0)
        {
            artifacts.Write(context.Request.Out, "arch-edges.mmd", MermaidDiagrams.ProjectEdges(added, removed));
            written.Add("arch-edges.mmd");
        }

        if (flows.Count > 0)
        {
            artifacts.Write(context.Request.Out, "request-flows.mmd", MermaidDiagrams.RequestFlows(flows));
            written.Add("request-flows.mmd");
        }

        return new GateResult(GateId.ArchFlows, Verdict.Pass, Actual: added.Count + removed.Count + flows.Count, Offenders: [.. written.Select(w => "diagram written: " + w)], Message: $"{added.Count.ToString(CultureInfo.InvariantCulture)} edge(s) added, {removed.Count.ToString(CultureInfo.InvariantCulture)} removed, {flows.Count.ToString(CultureInfo.InvariantCulture)} endpoint flow(s) in changed files (Mermaid, report only)");
    }

    private async Task<(IReadOnlyDictionary<string, IReadOnlySet<string>> Graph, IReadOnlyDictionary<string, string> Dirs)> GraphAsync(Context context)
    {
        if (context.Graph is not null)
        {
            return context.Graph.Value;
        }

        var projects = new Dictionary<string, string>(StringComparer.Ordinal);
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);
        var csprojs = (await source.HeadFilesAsync(context.Request.Repo, context.Token).ConfigureAwait(false)).Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
        var texts = await source.ReadManyAsync(context.Request.Repo, context.Request.Target, PrRevision.Head, csprojs, context.Token).ConfigureAwait(false);
        foreach (var path in csprojs)
        {
            if (texts.GetValueOrDefault(path) is { } text)
            {
                projects[path] = text;
                dirs[ProjectGraph.NameOf(path)] = path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;
            }
        }

        context.Graph = (ProjectGraph.Build(projects), dirs);
        return context.Graph.Value;
    }

    private static List<string> ChangedProjects(Context context, IReadOnlyDictionary<string, string> projectDirs)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in context.Changes)
        {
            if (change.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(ProjectGraph.NameOf(change.Path));
                continue;
            }

            var owner = projectDirs.Where(d => change.Path.StartsWith(d.Value.Length == 0 ? string.Empty : d.Value + "/", StringComparison.Ordinal))
                .OrderByDescending(d => d.Value.Length).Select(d => d.Key).FirstOrDefault();
            if (owner is not null)
            {
                result.Add(owner);
            }
        }

        return [.. result];
    }

    private async Task<IReadOnlyList<(string Path, string Text)>?> SourceFilesAsync(Context context)
    {
        if (context.Sources is not null)
        {
            return context.Sources;
        }

        var paths = (await source.HeadFilesAsync(context.Request.Repo, context.Token).ConfigureAwait(false)).Where(SourcePaths.IsCSharp).ToList();
        if (paths.Count > context.Options.MaxScannedFiles)
        {
            return null;
        }

        var texts = await source.ReadManyAsync(context.Request.Repo, context.Request.Target, PrRevision.Head, paths, context.Token).ConfigureAwait(false);
        var files = new List<(string, string)>();
        foreach (var path in paths)
        {
            if (texts.GetValueOrDefault(path) is { } text)
            {
                files.Add((path, text));
            }
        }

        context.Sources = files;
        return files;
    }

    private static GateResult TooManyFiles(GateId gate, Context context) =>
        new(gate, Verdict.Warn, Message: $"{gate} skipped: more than {context.Options.MaxScannedFiles.ToString(CultureInfo.InvariantCulture)} C# files to scan");

    private static bool IsReferencedByConvention(string name) =>
        !name.EndsWith("Controller", StringComparison.Ordinal) && name is not ("Program" or "Startup");

    private static IEnumerable<string> Directories(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            yield return string.Join('/', parts.Take(i));
        }
    }

    private Task<string?> ReadAsync(Context context, PrRevision revision, string path) =>
        source.ReadAsync(context.Request.Repo, context.Request.Target, revision, path, context.Token);

    private static GateResult Bypassed(GateId gate) => new(gate, Verdict.Skip, Message: PrRunner.BypassedMessage);

    private sealed class Context(PrRequest request, PrProcessOptions options, IReadOnlyList<FileChange> changes, CancellationToken token)
    {
        public PrRequest Request { get; } = request;

        public PrProcessOptions Options { get; } = options;

        public IReadOnlyList<FileChange> Changes { get; } = changes;

        public CancellationToken Token { get; } = token;

        public (IReadOnlyDictionary<string, IReadOnlySet<string>> Graph, IReadOnlyDictionary<string, string> Dirs)? Graph { get; set; }

        public IReadOnlyList<(string Path, string Text)>? Sources { get; set; }
    }
}
