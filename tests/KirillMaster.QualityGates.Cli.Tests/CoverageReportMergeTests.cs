using FluentAssertions;
using KirillMaster.QualityGates.Cli.Domain.Coverage;
using KirillMaster.QualityGates.Cli.Domain.Diff;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class CoverageReportMergeTests
{
    [Fact]
    [Trait("scenario", "US2-AS25")]
    public void Merge_keeps_a_file_that_has_no_recorded_lines()
    {
        var empty = new CoverageReport();
        empty.AddFile("src/Empty.cs");

        var merged = CoverageReport.Merge([empty]);

        merged.HasFile("src/Empty.cs").Should().BeTrue();
    }

    [Fact]
    [Trait("scenario", "US2-AS1")]
    public void Core_offenders_are_listed_in_file_path_order_not_insertion_order()
    {
        var changed = new ChangedLines();
        var cov = new CoverageReport();
        foreach (var file in new[] { "core/Z.cs", "core/A.cs" })
        {
            changed.Add(file, 1);
            cov.Record(file, 1, 0);
        }

        var core = DiffCoverageGate.Evaluate(changed, cov, p => Layer.Core, GatesConfig.Default)
            .Single(x => x.Layer == ResultLayer.Core);

        core.Offenders.Should().Equal("core/A.cs:1", "core/Z.cs:1");
    }
}
