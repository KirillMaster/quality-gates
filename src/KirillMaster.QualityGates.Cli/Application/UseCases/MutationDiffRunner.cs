using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain;
using KirillMaster.QualityGates.Cli.Domain.Diff;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Application.UseCases;

public sealed record MutationDiffRequest(
    string Repo,
    string Target,
    string Source,
    string Out,
    IReadOnlyCollection<QualityGroup>? Skip = null,
    string? BypassedBy = null);

public sealed record MutationDiffOutcome(int ExitCode, RunReport Report);

public sealed class MutationDiffRunner(
    IConfigStore configs,
    IGitClient git,
    IProjectLocator locator,
    IPrFiles files,
    IMutationRunner runner,
    IMutationVerdictStore verdicts)
{
    public async Task<MutationDiffOutcome> RunAsync(MutationDiffRequest request, CancellationToken cancellationToken = default)
    {
        var skipped = request.Skip ?? [];
        var skippedNames = skipped.Select(g => g.ToString().ToLowerInvariant()).Order(StringComparer.Ordinal).ToList();
        var policy = BranchMatrix.Resolve(request.Source, request.Target);

        var (config, configError) = ResolveConfig(request);
        if (configError is not null)
        {
            return Failure(request, policy, skippedNames, configError);
        }

        if (skipped.Contains(QualityGroup.Mutation))
        {
            return Finish(request, policy, skippedNames, [new GateResult(GateId.MutationDiff, Verdict.Skip, ResultLayer.All, Message: PrRunner.BypassedMessage)]);
        }

        if (policy == Policy.Skip)
        {
            return Finish(request, policy, skippedNames, MutationDiffGate.Evaluate([], _ => Layer.Excluded, config, policy));
        }

        var layers = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        Layer LayerOf(string file) => LayerOfFile(request, config, layers, file);

        var changed = DiffParser.Parse(await git.DiffAsync(request.Repo, request.Target, cancellationToken).ConfigureAwait(false));
        if (!changed.Files.Any(f => LayerOf(f) is Layer.Core or Layer.Adapter))
        {
            return Finish(request, policy, skippedNames, [new GateResult(GateId.MutationDiff, Verdict.NotApplicable, ResultLayer.All, Message: "no production code changed")]);
        }

        var (run, runError) = await RunMutationAsync(request, config).ConfigureAwait(false);
        return runError is not null
            ? Failure(request, policy, skippedNames, runError)
            : Finish(request, policy, skippedNames, MutationDiffGate.Evaluate(run!.Mutants, LayerOf, config, policy, run.TimedOut));
    }

    private async Task<(MutationRun? Run, string? Error)> RunMutationAsync(MutationDiffRequest request, GatesConfig config)
    {
        using var budget = MutationBudget.CreateDiffSource(config);
        try
        {
            return (await runner.RunAsync(request.Repo, request.Target, budget.Token).ConfigureAwait(false), null);
        }
        catch (MutationReportException ex)
        {
            return (null, ex.Message);
        }
    }

    private Layer LayerOfFile(MutationDiffRequest request, GatesConfig config, Dictionary<string, Layer> cache, string file)
    {
        if (!cache.TryGetValue(file, out var layer))
        {
            layer = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? locator.Locate(request.Repo, file, config.LayerOverrides)?.Layer ?? Layer.Excluded
                : Layer.Excluded;
            cache[file] = layer;
        }

        return layer;
    }

    private (GatesConfig Config, string? Error) ResolveConfig(MutationDiffRequest request)
    {
        var configPath = Path.Combine(request.Repo, "quality", "gates.json");
        if (!files.Exists(configPath))
        {
            return (GatesConfig.Default, null);
        }

        var loaded = configs.Load(configPath);
        return loaded.Error is { } error
            ? (GatesConfig.Default, $"invalid gates.json, field {error.FieldPath}: {error.Message}")
            : (loaded.Config!, null);
    }

    private MutationDiffOutcome Finish(MutationDiffRequest request, Policy policy, IReadOnlyList<string> skippedNames, IReadOnlyList<GateResult> results)
    {
        var report = new RunReport(RunMode.MutationDiff, policy, results, 0, request.Source, request.Target, request.BypassedBy, skippedNames);
        return Write(request, report with { ExitCode = ExitCodeMapper.Map(report) });
    }

    private MutationDiffOutcome Failure(MutationDiffRequest request, Policy policy, IReadOnlyList<string> skippedNames, string error) => Write(
        request,
        new RunReport(RunMode.MutationDiff, policy, [], ExitCodeMapper.Error, request.Source, request.Target, request.BypassedBy, skippedNames, error));

    private MutationDiffOutcome Write(MutationDiffRequest request, RunReport report)
    {
        verdicts.Write(request.Out, report);
        return new MutationDiffOutcome(report.ExitCode, report);
    }
}
