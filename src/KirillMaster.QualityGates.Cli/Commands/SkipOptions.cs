using System.CommandLine;
using KirillMaster.QualityGates.Cli.Application.UseCases;

namespace KirillMaster.QualityGates.Cli.Commands;

internal sealed class SkipOptions
{
    private static readonly QualityGroup[] All = [QualityGroup.Static, QualityGroup.Coverage, QualityGroup.Mutation, QualityGroup.Format, QualityGroup.Duplication, QualityGroup.BaselineGrowth, QualityGroup.Coupling, QualityGroup.PrSize, QualityGroup.ArchDelta, QualityGroup.Complexity, QualityGroup.Toggles, QualityGroup.DeadPublic, QualityGroup.FanOut, QualityGroup.Reports, QualityGroup.FeatureReadme, QualityGroup.Flows, QualityGroup.TestDiff, QualityGroup.IlGraph, QualityGroup.BranchAge, QualityGroup.BuildTrend, QualityGroup.MovedCoverage];

    private static readonly Dictionary<string, QualityGroup[]> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["all"] = All,
        ["static"] = [QualityGroup.Static],
        ["coverage"] = [QualityGroup.Coverage],
        ["mutation"] = [QualityGroup.Mutation],
        ["format"] = [QualityGroup.Format],
        ["duplication"] = [QualityGroup.Duplication],
        ["baselinegrowth"] = [QualityGroup.BaselineGrowth],
        ["coupling"] = [QualityGroup.Coupling],
        ["prsize"] = [QualityGroup.PrSize],
        ["archdelta"] = [QualityGroup.ArchDelta],
        ["complexity"] = [QualityGroup.Complexity],
        ["toggles"] = [QualityGroup.Toggles],
        ["deadpublic"] = [QualityGroup.DeadPublic],
        ["fanout"] = [QualityGroup.FanOut],
        ["reports"] = [QualityGroup.Reports],
        ["featurereadme"] = [QualityGroup.FeatureReadme],
        ["flows"] = [QualityGroup.Flows],
        ["testdiff"] = [QualityGroup.TestDiff],
        ["ilgraph"] = [QualityGroup.IlGraph],
        ["branchage"] = [QualityGroup.BranchAge],
        ["buildtrend"] = [QualityGroup.BuildTrend],
        ["movedcoverage"] = [QualityGroup.MovedCoverage]
    };

    public Option<string[]> Skip { get; } = new("--skip")
    {
        Description = "Disable gate groups: static, coverage, mutation, format, duplication, baselinegrowth, coupling, prsize, archdelta, complexity, toggles, deadpublic, fanout, reports, featurereadme, flows, testdiff, ilgraph, branchage, buildtrend, movedcoverage or all (comma separated)",
        AllowMultipleArgumentsPerToken = true
    };

    public Option<string?> BypassBy { get; } = new("--bypass-by") { Description = "User who bypassed the gates (implies --skip all)" };

    public void AddTo(Command command)
    {
        command.Options.Add(Skip);
        command.Options.Add(BypassBy);
    }

    public (HashSet<QualityGroup> Groups, string? BypassedBy) Read(ParseResult parse)
    {
        var bypassedBy = parse.GetValue(BypassBy);
        var groups = ParseGroups(parse.GetValue(Skip) ?? []);
        if (!string.IsNullOrWhiteSpace(bypassedBy) && groups.Count == 0)
        {
            groups = [.. All];
        }

        return (groups, string.IsNullOrWhiteSpace(bypassedBy) ? null : bypassedBy);
    }

    private static HashSet<QualityGroup> ParseGroups(IEnumerable<string> values)
    {
        var groups = new HashSet<QualityGroup>();
        foreach (var token in values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!ByName.TryGetValue(token, out var named))
            {
                throw new ArgumentException($"unknown --skip group '{token}' (expected static, coverage, mutation, format, duplication, baselinegrowth, coupling, prsize, archdelta, complexity, toggles, deadpublic, fanout, reports, featurereadme, flows, testdiff, ilgraph, branchage, buildtrend, movedcoverage or all)");
            }

            groups.UnionWith(named);
        }

        return groups;
    }
}
