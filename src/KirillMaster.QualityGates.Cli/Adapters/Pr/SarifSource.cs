using KirillMaster.QualityGates.Cli.Adapters.Sarif;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Adapters.Pr;

public sealed class SarifSource : ISarifSource
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Aggregate(IReadOnlyList<string> files) => SarifReader.Aggregate(files);

    public IReadOnlyList<Finding> Read(IReadOnlyList<string> files) => SarifReader.ReadFindings(files);
}
