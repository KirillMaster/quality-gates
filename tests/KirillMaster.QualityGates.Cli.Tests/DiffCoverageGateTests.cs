using FluentAssertions;
using KirillMaster.QualityGates.Cli.Domain.Coverage;
using KirillMaster.QualityGates.Cli.Domain.Diff;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class DiffCoverageGateTests
{
    private static Layer ByPath(string p) =>
        p.StartsWith("core/", StringComparison.Ordinal) ? Layer.Core
        : p.StartsWith("adapter/", StringComparison.Ordinal) ? Layer.Adapter
        : p.StartsWith("tests/", StringComparison.Ordinal) ? Layer.Test
        : Layer.Excluded;

    private static (ChangedLines, CoverageReport) Build(string file, int total, int covered)
    {
        var changed = new ChangedLines();
        var cov = new CoverageReport();
        for (var i = 1; i <= total; i++)
        {
            changed.Add(file, i);
            cov.Record(file, i, i <= covered ? 1 : 0);
        }

        return (changed, cov);
    }

    private static GateResult Layered(IReadOnlyList<GateResult> r, ResultLayer l) => r.Single(x => x.Layer == l);

    private static IReadOnlyList<GateResult> Run(ChangedLines c, CoverageReport cov, GatesConfig? cfg = null) =>
        DiffCoverageGate.Evaluate(c, cov, ByPath, cfg ?? GatesConfig.Default);

    [Fact]
    [Trait("scenario", "US2-AS1")]
    public void Core_below_threshold_fails_and_lists_exact_uncovered_lines()
    {
        var (c, cov) = Build("core/A.cs", 20, 17);

        var core = Layered(Run(c, cov), ResultLayer.Core);

        core.Verdict.Should().Be(Verdict.Fail);
        core.Actual.Should().Be(85);
        core.Threshold.Should().Be(90);
        core.Offenders.Should().Equal("core/A.cs:18", "core/A.cs:19", "core/A.cs:20");
    }

    [Theory]
    [Trait("scenario", "US2-AS28")]
    [InlineData(89, Verdict.Fail)]
    [InlineData(90, Verdict.Pass)]
    [InlineData(100, Verdict.Pass)]
    public void Core_threshold_boundary(int pct, Verdict expected)
    {
        var (c, cov) = Build("core/A.cs", 100, pct);
        Layered(Run(c, cov), ResultLayer.Core).Verdict.Should().Be(expected);
    }

    [Fact]
    [Trait("scenario", "US2-AS28")]
    public void Exactly_nine_of_ten_passes()
    {
        var (c, cov) = Build("core/A.cs", 10, 9);
        Layered(Run(c, cov), ResultLayer.Core).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    [Trait("scenario", "US2-AS2")]
    public void Integration_disabled_downgrades_adapter_to_warn()
    {
        var (c, cov) = Build("adapter/A.cs", 10, 5);
        var cfg = GatesConfig.Default with { IntegrationTestsEnabled = false };

        var adapter = Layered(Run(c, cov, cfg), ResultLayer.Adapter);

        adapter.Verdict.Should().Be(Verdict.Warn);
    }

    [Fact]
    [Trait("scenario", "US2-AS2")]
    public void Integration_disabled_does_not_downgrade_core()
    {
        var (c, cov) = Build("core/A.cs", 10, 5);
        var cfg = GatesConfig.Default with { IntegrationTestsEnabled = false };
        Layered(Run(c, cov, cfg), ResultLayer.Core).Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    [Trait("scenario", "US2-AS29")]
    public void Adapter_below_threshold_with_integration_enabled_fails()
    {
        var (c, cov) = Build("adapter/A.cs", 100, 79);
        Layered(Run(c, cov), ResultLayer.Adapter).Verdict.Should().Be(Verdict.Fail);
    }

    [Theory]
    [Trait("scenario", "US2-AS30")]
    [InlineData(true)]
    [InlineData(false)]
    public void Adapter_at_threshold_passes_regardless_of_integration(bool enabled)
    {
        var (c, cov) = Build("adapter/A.cs", 10, 8);
        var cfg = GatesConfig.Default with { IntegrationTestsEnabled = enabled };
        Layered(Run(c, cov, cfg), ResultLayer.Adapter).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    [Trait("scenario", "US2-AS31")]
    public void No_cs_changes_is_not_applicable_for_both_layers()
    {
        var diff = File.ReadAllText(Fx.Path("diffs", "mixed-extensions.diff")).Replace("src/A.cs", "src/A.txt");

        var r = Run(DiffParser.Parse(diff), new CoverageReport());

        r.Should().HaveCount(2);
        r.Should().OnlyContain(x => x.Verdict == Verdict.NotApplicable);
    }

    [Fact]
    [Trait("scenario", "US2-AS32")]
    public void Layers_are_evaluated_independently()
    {
        var (c, cov) = Build("core/A.cs", 20, 19);
        var (c2, cov2) = Build("adapter/B.cs", 10, 6);
        foreach (var l in c2.LinesOf("adapter/B.cs"))
        {
            c.Add("adapter/B.cs", l);
        }

        var r = Run(c, CoverageReport.Merge([cov, cov2]));

        r.Should().HaveCount(2);
        Layered(r, ResultLayer.Core).Verdict.Should().Be(Verdict.Pass);
        Layered(r, ResultLayer.Adapter).Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    [Trait("scenario", "US2-AS33")]
    public void Test_and_excluded_files_are_ignored()
    {
        var (c, cov) = Build("core/A.cs", 10, 10);
        foreach (var f in new[] { "tests/T.cs", "other/X.cs" })
        {
            for (var i = 1; i <= 5; i++)
            {
                c.Add(f, i);
                cov.Record(f, i, 0);
            }
        }

        var r = Run(c, cov);

        Layered(r, ResultLayer.Core).Verdict.Should().Be(Verdict.Pass);
        Layered(r, ResultLayer.Core).Actual.Should().Be(100);
        Layered(r, ResultLayer.Core).Offenders.Should().BeEmpty();
        Layered(r, ResultLayer.Adapter).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    [Trait("scenario", "US2-AS34")]
    public void Configured_threshold_replaces_default()
    {
        var (c, cov) = Build("core/A.cs", 100, 92);
        var cfg = GatesConfig.Default with { DiffCoverage = new DiffCoverageThresholds(Core: 95) };

        var core = Layered(Run(c, cov, cfg), ResultLayer.Core);

        core.Verdict.Should().Be(Verdict.Fail);
        core.Threshold.Should().Be(95);
    }

    [Fact]
    [Trait("scenario", "US2-AS35")]
    public void No_size_limit_on_large_diffs()
    {
        var (c, cov) = Build("core/A.cs", 5000, 5000);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var core = Layered(Run(c, cov), ResultLayer.Core);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        core.Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    [Trait("scenario", "US2-AS36")]
    public void Noncoverable_lines_excluded_and_unreported_files_fully_uncovered()
    {
        var changed = new ChangedLines();
        foreach (var l in new[] { 10, 11, 12 })
        {
            changed.Add("core/A.cs", l);
        }

        foreach (var l in new[] { 1, 2, 3 })
        {
            changed.Add("core/Missing.cs", l);
        }

        var cov = new CoverageReport();
        cov.Record("core/A.cs", 10, 1);
        cov.Record("core/A.cs", 11, 1);

        var core = Layered(Run(changed, cov), ResultLayer.Core);

        core.Offenders.Should().NotContain("core/A.cs:12");
        core.Offenders.Should().Equal("core/Missing.cs:1", "core/Missing.cs:2", "core/Missing.cs:3");
        core.Actual.Should().Be(40);
        core.Verdict.Should().Be(Verdict.Fail);
    }

    private static string? ProjectByDir(string p) => p.StartsWith("core/Covered/", StringComparison.Ordinal) ? "Covered"
        : p.StartsWith("core/Untested/", StringComparison.Ordinal) ? "Untested" : null;

    [Fact]
    public void Unreported_file_in_project_with_reported_files_is_skipped()
    {
        var (c, cov) = Build("core/Covered/A.cs", 10, 10);
        for (var i = 1; i <= 7; i++)
        {
            c.Add("core/Covered/Verdict.cs", i);
        }

        var core = Layered(DiffCoverageGate.Evaluate(c, cov, ByPath, GatesConfig.Default, ProjectByDir), ResultLayer.Core);

        core.Verdict.Should().Be(Verdict.Pass);
        core.Actual.Should().Be(100);
        core.Offenders.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Unreported_file_in_project_with_nothing_reported_stays_offender()
    {
        var (c, cov) = Build("core/Covered/A.cs", 10, 10);
        for (var i = 1; i <= 3; i++)
        {
            c.Add("core/Untested/B.cs", i);
        }

        var core = Layered(DiffCoverageGate.Evaluate(c, cov, ByPath, GatesConfig.Default, ProjectByDir), ResultLayer.Core);

        core.Verdict.Should().Be(Verdict.Fail);
        core.Offenders.Should().Equal("core/Untested/B.cs:1", "core/Untested/B.cs:2", "core/Untested/B.cs:3");
    }

    [Fact]
    public void Unreported_file_without_project_mapping_stays_offender()
    {
        var (c, cov) = Build("core/Covered/A.cs", 10, 10);
        c.Add("core/Other/C.cs", 1);

        var core = Layered(DiffCoverageGate.Evaluate(c, cov, ByPath, GatesConfig.Default, ProjectByDir), ResultLayer.Core);

        core.Offenders.Should().Equal("core/Other/C.cs:1");
    }
}
