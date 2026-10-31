using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Application;

public static class MutationBudget
{
    public static TimeSpan Diff(GatesConfig config) => TimeSpan.FromMinutes(config.Mutation.DiffBudgetMinutes);

    public static CancellationTokenSource CreateDiffSource(GatesConfig config)
    {
        var source = new CancellationTokenSource();
        source.CancelAfter(Diff(config));
        return source;
    }
}
