using KirillMaster.QualityGates.Cli.Domain.Coverage;
using KirillMaster.QualityGates.Cli.Domain.Diff;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

public static class DiffCoverageGate
{
    public static IReadOnlyList<GateResult> Evaluate(
        ChangedLines changed,
        CoverageReport coverage,
        Func<string, Layer> layerOf,
        GatesConfig config,
        Func<string, string?>? projectOf = null)
    {
        var instrumented = InstrumentedProjects(coverage, projectOf);
        return
        [
            EvaluateLayer(Layer.Core, ResultLayer.Core, config.DiffCoverage.Core, changed, coverage, layerOf, projectOf, instrumented, downgradeToWarn: false),
            EvaluateLayer(Layer.Adapter, ResultLayer.Adapter, config.DiffCoverage.Adapter, changed, coverage, layerOf, projectOf, instrumented,
                downgradeToWarn: !config.IntegrationTestsEnabled)
        ];
    }

    private static HashSet<string> InstrumentedProjects(CoverageReport coverage, Func<string, string?>? projectOf)
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (projectOf is null)
        {
            return projects;
        }

        foreach (var file in coverage.Files)
        {
            if (projectOf(file) is { } project)
            {
                projects.Add(project);
            }
        }

        return projects;
    }

    private static GateResult EvaluateLayer(
        Layer layer,
        ResultLayer resultLayer,
        double threshold,
        ChangedLines changed,
        CoverageReport coverage,
        Func<string, Layer> layerOf,
        Func<string, string?>? projectOf,
        HashSet<string> instrumented,
        bool downgradeToWarn)
    {
        var covered = 0;
        var total = 0;
        var offenders = new List<string>();

        foreach (var file in FilesOfLayer(changed, layerOf, layer))
        {
            var reported = coverage.HasFile(file);
            if (!reported && projectOf?.Invoke(file) is { } owner && instrumented.Contains(owner))
            {
                continue;
            }

            foreach (var line in changed.LinesOf(file))
            {
                var hits = 0;
                var coverable = reported && coverage.TryGetHits(file, line, out hits);
                if (reported && !coverable)
                {
                    continue;
                }

                total++;
                if (coverable && hits > 0)
                {
                    covered++;
                }
                else
                {
                    offenders.Add($"{file}:{line}");
                }
            }
        }

        if (total == 0)
        {
            return new GateResult(GateId.DiffCoverage, Verdict.NotApplicable, resultLayer, null, threshold);
        }

        var actual = covered * 100.0 / total;
        var passes = (decimal)covered * 100m >= (decimal)threshold * total;
        var verdict = passes ? Verdict.Pass : downgradeToWarn ? Verdict.Warn : Verdict.Fail;
        return new GateResult(GateId.DiffCoverage, verdict, resultLayer, actual, threshold, offenders);
    }

    private static List<string> FilesOfLayer(ChangedLines changed, Func<string, Layer> layerOf, Layer layer)
    {
        var files = new List<string>();
        foreach (var f in changed.Files)
        {
            if (layerOf(f) == layer)
            {
                files.Add(f);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
