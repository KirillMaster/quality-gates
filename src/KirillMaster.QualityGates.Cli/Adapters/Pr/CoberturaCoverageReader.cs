using KirillMaster.QualityGates.Cli.Adapters.Coverage;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Coverage;

namespace KirillMaster.QualityGates.Cli.Adapters.Pr;

public sealed class CoberturaCoverageReader : ICoverageReader
{
    public CoverageReport ReadMerged(IReadOnlyList<string> files, string repoRoot) => CoberturaReader.ReadMerged(files, repoRoot);

    public CoverageReport ReadMergedFromOtherRoot(IReadOnlyList<string> files, string repoRoot, string? reportRoot) =>
        CoverageReport.Merge(files.Select(f => CoberturaReader.Read(f, repoRoot, reportRoot, detectRoot: true)));
}
