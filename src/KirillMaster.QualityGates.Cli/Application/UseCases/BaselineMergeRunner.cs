using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Gates;

namespace KirillMaster.QualityGates.Cli.Application.UseCases;

public sealed record BaselineMergeResult(string Path, int Added);

public sealed class BaselineMergeRunner(IBaselineStore baselines, ISarifSource sarif, IPrFiles files)
{
    public BaselineMergeResult MergeNew(string repo, IReadOnlyList<string> sarifGlobs)
    {
        var path = Path.Combine(repo, "quality", "baseline.json");
        var existing = baselines.Load(path)
                       ?? throw new InvalidOperationException($"no baseline found at {path}; run a PR build to bootstrap it first");
        var sarifFiles = files.Expand(sarifGlobs);
        if (sarifFiles.Count == 0)
        {
            throw new InvalidOperationException($"no SARIF files were found for: {string.Join(", ", sarifGlobs)}");
        }

        var change = BaselineEvolution.AddMissing(existing, sarif.Aggregate(sarifFiles));
        if (change.Entries > 0)
        {
            baselines.Save(path, change.Baseline);
        }

        return new BaselineMergeResult(path, change.Entries);
    }
}
