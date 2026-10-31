using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Adapters.Pr;

public sealed class JsonMutationVerdictStore : IMutationVerdictStore
{
    public const string FileName = "mutation-verdict.json";

    public void Write(string outDirectory, RunReport report) => new JsonReportStore().Write(outDirectory, report, FileName);
}
