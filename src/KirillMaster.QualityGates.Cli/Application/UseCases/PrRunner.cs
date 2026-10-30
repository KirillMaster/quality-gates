using System.Globalization;
using System.Text.Json;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Coverage;
using KirillMaster.QualityGates.Cli.Domain.Diff;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Application.UseCases;

public enum QualityGroup { Static, Coverage, Mutation, Format, Duplication, BaselineGrowth, Coupling, PrSize, ArchDelta, Complexity, Toggles, DeadPublic, FanOut, Reports, FeatureReadme, Flows, TestDiff, IlGraph, BranchAge, BuildTrend, MovedCoverage }

public sealed record PrRequest(
    string Repo,
    string Target,
    string Source,
    IReadOnlyList<string> CoverageGlobs,
    IReadOnlyList<string> SarifGlobs,
    string Out,
    bool? IntegrationEnabled = null,
    IReadOnlyCollection<QualityGroup>? Skip = null,
    string? BypassedBy = null,
    bool BranchBuild = false,
    PrDeltaOptions? Delta = null,
    PrProcessOptions? Process = null);

public sealed record PrOutcome(int ExitCode, RunReport Report);

public sealed class PrRunner(
    IConfigStore configs,
    IBaselineStore baselines,
    IGitClient git,
    ICoverageReader coverage,
    ISarifSource sarif,
    IProjectLocator locator,
    IPrFiles files,
    IReportStore reports,
    TimeProvider clock,
    IFormatChecker? format = null,
    IDuplicationScanner? duplication = null,
    IDuplicationBaselineStore? duplicationBaselines = null,
    PrDeltaGates? delta = null,
    PrProcessGates? process = null)
{
    public const string BypassedMessage = "skip (bypassed)";

    public async Task<PrOutcome> RunAsync(PrRequest request, CancellationToken cancellationToken = default)
    {
        var skipped = request.Skip ?? [];
        var skippedNames = skipped.Select(g => g.ToString().ToLowerInvariant()).Order(StringComparer.Ordinal).ToList();
        var skipStatic = skipped.Contains(QualityGroup.Static);
        var skipCoverage = skipped.Contains(QualityGroup.Coverage);
        var skipMutation = skipped.Contains(QualityGroup.Mutation);

        var (config, configError) = ResolveConfig(request);
        if (configError is not null)
        {
            return Failure(request, skippedNames, configError);
        }

        var coverageFiles = skipCoverage ? [] : files.Expand(request.CoverageGlobs);
        if (!skipCoverage && coverageFiles.Count == 0)
        {
            return Failure(request, skippedNames, $"no coverage files were found for: {string.Join(", ", request.CoverageGlobs)}");
        }

        var sarifFiles = skipStatic ? [] : files.Expand(request.SarifGlobs);
        if (!skipStatic && sarifFiles.Count == 0)
        {
            return Failure(request, skippedNames, $"no SARIF files were found for: {string.Join(", ", request.SarifGlobs)}");
        }

        var layers = new LayerLookup(locator, request.Repo, config.LayerOverrides);
        var merged = skipCoverage ? null : coverage.ReadMerged(coverageFiles, request.Repo);

        var findings = skipStatic ? [] : sarif.Read(sarifFiles);

        RatchetOutcome? ratchet = null;
        if (!skipStatic || !skipCoverage)
        {
            try
            {
                ratchet = await EvaluateRatchetAsync(request, merged, layers, skipStatic ? null : sarifFiles, findings, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return Failure(request, skippedNames, $"malformed JSON in quality/baseline.json: {ex.Message}");
            }
        }

        var diffText = merged is not null || findings.Count > 0
            ? await git.DiffAsync(request.Repo, request.Target, cancellationToken).ConfigureAwait(false)
            : null;
        var changed = diffText is null ? null : DiffParser.Parse(diffText);

        var results = new List<GateResult>
        {
            skipStatic ? Bypassed(GateId.StaticRatchet) : ratchet!.Static,
            skipStatic ? Bypassed(GateId.NewCodeViolations) : NewCodeViolationGate.Evaluate(changed ?? new ChangedLines(), findings, request.Repo)
        };
        results.AddRange(EvaluateCoverage(config, merged, request.Process?.EffectiveKind == ChangeKind.Structural && changed is not null && diffText is not null ? WithoutMovedLines(changed, diffText) : changed, layers, ratchet));

        if (skipMutation)
        {
            results.Add(Bypassed(GateId.MutationDiff));
        }

        if (format is not null)
        {
            results.Add(await RunFormatAsync(request, skipped.Contains(QualityGroup.Format), cancellationToken).ConfigureAwait(false));
        }

        if (duplication is not null && duplicationBaselines is not null)
        {
            results.Add(await RunDuplicationAsync(request, skipped.Contains(QualityGroup.Duplication), cancellationToken).ConfigureAwait(false));
        }

        if (delta is not null)
        {
            try
            {
                results.AddRange(await delta.EvaluateAsync(request, cancellationToken).ConfigureAwait(false));
            }
            catch (PrInputException ex)
            {
                return Failure(request, skippedNames, ex.Message);
            }
        }

        if (process is not null)
        {
            try
            {
                results.AddRange(await process.EvaluateAsync(request, cancellationToken).ConfigureAwait(false));
            }
            catch (PrInputException ex)
            {
                return Failure(request, skippedNames, ex.Message);
            }
        }

        var report = new RunReport(RunMode.Pr, Policy.Blocking, results, 0, request.Source, request.Target, request.BypassedBy, skippedNames);
        return Write(request, report with { ExitCode = ExitCodeMapper.Map(report) });
    }

    private async Task<GateResult> RunFormatAsync(PrRequest request, bool skip, CancellationToken cancellationToken)
    {
        if (skip || format is null)
        {
            return Bypassed(GateId.Format);
        }

        var started = clock.GetTimestamp();
        var unformatted = await format.UnformattedFilesAsync(request.Repo, cancellationToken).ConfigureAwait(false);
        var result = FormatGate.Evaluate(unformatted, request.BranchBuild);
        return result with { Message = WithDuration(result.Message, started) };
    }

    private async Task<GateResult> RunDuplicationAsync(PrRequest request, bool skip, CancellationToken cancellationToken)
    {
        if (skip || duplication is null || duplicationBaselines is null)
        {
            return Bypassed(GateId.Duplication);
        }

        var started = clock.GetTimestamp();
        var blocks = await duplication.ScanAsync(request.Repo, cancellationToken).ConfigureAwait(false);
        var baseline = duplicationBaselines.Load(Path.Combine(request.Repo, "quality", "duplication-baseline.json"));
        var result = DuplicationGate.Evaluate(blocks, baseline);
        if (baseline is null)
        {
            duplicationBaselines.Save(Path.Combine(request.Out, "duplication-baseline.json"), blocks.Select(b => b.Fingerprint).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
        }

        return result with { Message = WithDuration(result.Message, started) };
    }

    private string WithDuration(string? message, long started) =>
        $"{message} [{clock.GetElapsedTime(started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s]";

    private (GatesConfig Config, string? Error) ResolveConfig(PrRequest request)
    {
        var config = GatesConfig.Default;
        var configPath = Path.Combine(request.Repo, "quality", "gates.json");
        if (files.Exists(configPath))
        {
            var loaded = configs.Load(configPath);
            if (loaded.Error is { } configError)
            {
                return (config, $"invalid gates.json, field {configError.FieldPath}: {configError.Message}");
            }

            config = loaded.Config!;
        }

        if (request.IntegrationEnabled is { } integration)
        {
            config = config with { IntegrationTestsEnabled = integration };
        }

        return (config, null);
    }

    private async Task<RatchetOutcome> EvaluateRatchetAsync(
        PrRequest request,
        CoverageReport? merged,
        LayerLookup layers,
        IReadOnlyList<string>? sarifFiles,
        IReadOnlyList<Finding> findings,
        CancellationToken cancellationToken)
    {
        var current = new Baseline(
            await git.HeadAsync(request.Repo, cancellationToken).ConfigureAwait(false),
            clock.GetUtcNow().ToString("o", CultureInfo.InvariantCulture),
            merged is null ? [] : Measure(merged, layers),
            sarifFiles is null ? new Dictionary<string, IReadOnlyDictionary<string, int>>() : sarif.Aggregate(sarifFiles));
        var previous = baselines.Load(Path.Combine(request.Repo, "quality", "baseline.json"));
        var ratchet = BaselineRatchet.Evaluate(previous, current);
        if (ratchet.InitialBaseline is not null)
        {
            baselines.Save(Path.Combine(request.Out, "baseline.json"), ratchet.InitialBaseline);
        }

        if (sarifFiles is null)
        {
            return ratchet;
        }

        reports.WriteDebt(request.Out, new DebtReport(findings, previous?.Warnings, current.Warnings));
        if (previous is null || BaselineEvolution.Tighten(previous, current) is not { } tightened)
        {
            return ratchet;
        }

        baselines.Save(Path.Combine(request.Out, "baseline.json"), tightened.Baseline);
        var note = $"baseline tightened ({tightened.Entries.ToString(CultureInfo.InvariantCulture)} lower rule counts), tightened baseline.json written to the output directory";
        return ratchet with { Static = ratchet.Static with { Message = note } };
    }

    private static IReadOnlyList<GateResult> EvaluateCoverage(
        GatesConfig config,
        CoverageReport? merged,
        ChangedLines? changed,
        LayerLookup layers,
        RatchetOutcome? ratchet)
    {
        if (merged is null || changed is null)
        {
            return
            [
                Bypassed(GateId.DiffCoverage, ResultLayer.Core),
                Bypassed(GateId.DiffCoverage, ResultLayer.Adapter),
                Bypassed(GateId.CoverageRatchet)
            ];
        }

        var results = new List<GateResult>(DiffCoverageGate.Evaluate(changed, merged, layers.Of, config, f => layers.ProjectOf(f)?.Name));
        results.Add(ratchet!.Coverage);
        return results;
    }

    // CN-2: a structural change exempts only the lines the diff moved (MovedLineCoverage guards them); added lines still need diff coverage.
    private static ChangedLines WithoutMovedLines(ChangedLines changed, string diff)
    {
        var moved = MovedLines.NewSideKeys(diff);
        var kept = new ChangedLines();
        foreach (var file in changed.Files)
        {
            foreach (var line in changed.LinesOf(file).Where(l => !moved.Contains(MovedLines.Key(file, l))))
            {
                kept.Add(file, line);
            }
        }

        return kept;
    }

    private static GateResult Bypassed(GateId gate, ResultLayer? layer = null) =>
        new(gate, Verdict.Skip, layer, Message: BypassedMessage);

    private PrOutcome Failure(PrRequest request, IReadOnlyList<string> skippedNames, string error) => Write(
        request,
        new RunReport(RunMode.Pr, Policy.Blocking, [], ExitCodeMapper.Error, request.Source, request.Target, request.BypassedBy, skippedNames, error));

    private PrOutcome Write(PrRequest request, RunReport report)
    {
        reports.Write(request.Out, report);
        return new PrOutcome(report.ExitCode, report);
    }

    private static List<ProjectMetrics> Measure(CoverageReport merged, LayerLookup layers)
    {
        var totals = new SortedDictionary<string, (Layer Layer, int Covered, int Total)>(StringComparer.Ordinal);
        foreach (var file in merged.Files)
        {
            if (layers.ProjectOf(file) is not { } project || project.Layer is Layer.Test or Layer.Excluded)
            {
                continue;
            }

            var (_, covered, total) = totals.GetValueOrDefault(project.Name);
            foreach (var hits in merged.LinesOf(file).Values)
            {
                total++;
                if (hits > 0)
                {
                    covered++;
                }
            }

            totals[project.Name] = (project.Layer, covered, total);
        }

        var metrics = new List<ProjectMetrics>();
        foreach (var (name, (layer, covered, total)) in totals)
        {
            if (total > 0)
            {
                metrics.Add(new ProjectMetrics(name, layer, Math.Round(covered * 100.0 / total, 2)));
            }
        }

        return metrics;
    }

    private sealed class LayerLookup(IProjectLocator locator, string repo, IReadOnlyDictionary<string, Layer> overrides)
    {
        private readonly Dictionary<string, ProjectRef?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public ProjectRef? ProjectOf(string file)
        {
            if (!_cache.TryGetValue(file, out var project))
            {
                project = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? locator.Locate(repo, file, overrides) : null;
                _cache[file] = project;
            }

            return project;
        }

        public Layer Of(string file) => ProjectOf(file)?.Layer ?? Layer.Excluded;
    }
}
