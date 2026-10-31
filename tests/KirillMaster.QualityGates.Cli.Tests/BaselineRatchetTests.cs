using FluentAssertions;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class BaselineRatchetTests
{
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Counts(params (string Project, string Rule, int Count)[] items)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, int>>();
        foreach (var (project, rule, count) in items)
        {
            if (!result.TryGetValue(project, out var rules))
            {
                rules = new Dictionary<string, int>();
                result[project] = rules;
            }

            ((Dictionary<string, int>)rules)[rule] = count;
        }

        return result;
    }

    private static Baseline Make(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? warnings = null,
        double? coverage = null,
        string project = "Sample.Domain",
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? grandfathered = null)
    {
        var projects = coverage is null ? [] : new[] { new ProjectMetrics(project, Layer.Core, coverage.Value) };
        return new Baseline("abc123", "2026-10-21T00:00:00Z", projects, warnings ?? Counts(), grandfathered);
    }

    [Fact]
    [Trait("scenario", "US2-AS3")]
    public void New_warning_of_a_rule_fails_naming_rule_project_and_delta()
    {
        var baseline = Make(Counts(("Sample.Domain", "S1481", 0)));
        var current = Make(Counts(("Sample.Domain", "S1481", 1)));

        var outcome = BaselineRatchet.Evaluate(baseline, current);

        outcome.Static.Gate.Should().Be(GateId.StaticRatchet);
        outcome.Static.Verdict.Should().Be(Verdict.Fail);
        outcome.Static.Offenders.Should().Equal("Sample.Domain: S1481 +1");
    }

    [Theory]
    [Trait("scenario", "US2-AS38")]
    [InlineData(80.0, "Pass")]
    [InlineData(85.0, "Pass")]
    [InlineData(79.9, "Pass")]
    [InlineData(79.8, "Fail")]
    [InlineData(70.0, "Fail")]
    public void Coverage_ratchet_allows_a_drop_of_at_most_a_tenth_of_a_point(double currentCoverage, string expected)
    {
        var outcome = BaselineRatchet.Evaluate(Make(coverage: 80.0), Make(coverage: currentCoverage));

        outcome.Coverage.Gate.Should().Be(GateId.CoverageRatchet);
        outcome.Coverage.Verdict.Should().Be(Enum.Parse<Verdict>(expected));
        outcome.Static.Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    [Trait("scenario", "US2-AS38")]
    public void Coverage_offender_names_project_and_drop()
    {
        var outcome = BaselineRatchet.Evaluate(Make(coverage: 80.0), Make(coverage: 79.8));

        outcome.Coverage.Offenders.Should().Equal("Sample.Domain: line coverage 79.8 < baseline 80 (-0.2 pp)");
    }

    [Fact]
    [Trait("scenario", "US2-AS38")]
    public void Test_projects_are_not_coverage_ratcheted()
    {
        var baseline = new Baseline("c", "t", [new ProjectMetrics("Sample.Tests", Layer.Test, 90)], Counts());
        var current = new Baseline("c", "t", [new ProjectMetrics("Sample.Tests", Layer.Test, 10)], Counts());

        BaselineRatchet.Evaluate(baseline, current).Coverage.Verdict.Should().Be(Verdict.Pass);
    }

    [Theory]
    [Trait("scenario", "US2-AS39")]
    [InlineData(3)]
    [InlineData(2)]
    public void Fewer_or_equal_warnings_pass(int currentCount)
    {
        var baseline = Make(Counts(("Sample.Domain", "S1481", 3)));
        var current = Make(Counts(("Sample.Domain", "S1481", currentCount)));

        BaselineRatchet.Evaluate(baseline, current).Static.Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    [Trait("scenario", "US2-AS40")]
    public void Missing_baseline_warns_in_bootstrap_mode_and_produces_initial_baseline()
    {
        var current = Make(Counts(("Sample.Domain", "S1481", 4)), coverage: 71.5);

        var outcome = BaselineRatchet.Evaluate(null, current);

        outcome.Static.Verdict.Should().Be(Verdict.Warn);
        outcome.Coverage.Verdict.Should().Be(Verdict.Warn);
        outcome.Static.Message.Should().Contain("bootstrap");
        outcome.Coverage.Message.Should().Contain("bootstrap");
        outcome.InitialBaseline.Should().BeSameAs(current);
        outcome.InitialBaseline!.Projects.Should().ContainSingle().Which.LineCoverage.Should().Be(71.5);
        outcome.InitialBaseline.Warnings["Sample.Domain"]["S1481"].Should().Be(4);
    }

    [Fact]
    [Trait("scenario", "US2-AS40")]
    public void Existing_baseline_produces_no_initial_baseline() =>
        BaselineRatchet.Evaluate(Make(), Make()).InitialBaseline.Should().BeNull();

    [Fact]
    [Trait("scenario", "US2-AS41")]
    public void Warnings_in_a_project_absent_from_the_baseline_count_against_zero()
    {
        var baseline = Make(Counts(("Sample.Domain", "S1481", 1)));
        var current = Make(Counts(("Sample.Domain", "S1481", 1), ("Sample.New", "S1481", 2)));

        var outcome = BaselineRatchet.Evaluate(baseline, current);

        outcome.Static.Verdict.Should().Be(Verdict.Fail);
        outcome.Static.Offenders.Should().Equal("Sample.New: S1481 +2");
    }

    [Fact]
    [Trait("scenario", "US2-AS41")]
    public void New_project_coverage_is_compared_with_zero_and_passes()
    {
        var baseline = Make(coverage: 80.0);
        var current = new Baseline(
            "c", "t",
            [new ProjectMetrics("Sample.Domain", Layer.Core, 80.0), new ProjectMetrics("Sample.New", Layer.Core, 5.0)],
            Counts());

        BaselineRatchet.Evaluate(baseline, current).Coverage.Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    [Trait("scenario", "US2-AS42")]
    public void Several_increased_rules_are_all_listed()
    {
        var baseline = Make(Counts(("Sample.Adapters", "S1481", 1), ("Sample.Adapters", "CA2012", 0)));
        var current = Make(Counts(("Sample.Adapters", "S1481", 2), ("Sample.Adapters", "CA2012", 1)));

        var outcome = BaselineRatchet.Evaluate(baseline, current);

        outcome.Static.Verdict.Should().Be(Verdict.Fail);
        outcome.Static.Offenders.Should().Equal("Sample.Adapters: CA2012 +1", "Sample.Adapters: S1481 +1");
    }

    [Fact]
    [Trait("scenario", "US2-AS45")]
    public void Grandfathered_count_may_only_decrease()
    {
        var baseline = Make(grandfathered: Counts(("Sample.Adapters", "RS0030", 1)));

        Verdict Run(int sites) => BaselineRatchet
            .Evaluate(baseline, Make(grandfathered: Counts(("Sample.Adapters", "RS0030", sites)))).Static.Verdict;

        Run(1).Should().Be(Verdict.Pass);
        Run(0).Should().Be(Verdict.Pass);
        Run(2).Should().Be(Verdict.Fail);
    }

    [Fact]
    [Trait("scenario", "US2-AS45")]
    public void Grandfathered_increase_is_labelled_in_the_offender()
    {
        var baseline = Make(grandfathered: Counts(("Sample.Adapters", "RS0030", 1)));
        var current = Make(grandfathered: Counts(("Sample.Adapters", "RS0030", 2)));

        BaselineRatchet.Evaluate(baseline, current).Static.Offenders
            .Should().Equal("Sample.Adapters: RS0030 (grandfathered) +1");
    }

    [Fact]
    [Trait("scenario", "US2-AS45")]
    public void Baseline_without_grandfathered_section_counts_sites_against_zero()
    {
        var current = Make(grandfathered: Counts(("Sample.Adapters", "RS0030", 1)));

        BaselineRatchet.Evaluate(Make(), current).Static.Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    [Trait("scenario", "US2-AS42")]
    public void Static_actual_is_the_largest_single_rule_delta()
    {
        var baseline = Make(Counts(("Sample.Domain", "S1481", 0), ("Sample.Domain", "CA2012", 0)));
        var current = Make(Counts(("Sample.Domain", "S1481", 1), ("Sample.Domain", "CA2012", 3)));

        BaselineRatchet.Evaluate(baseline, current).Static.Actual.Should().Be(3);
    }

    [Fact]
    [Trait("scenario", "US2-AS38")]
    public void Coverage_baseline_lookup_uses_the_first_entry_for_a_project()
    {
        var baseline = new Baseline(
            "c", "t",
            [new ProjectMetrics("Sample.Domain", Layer.Core, 80.0), new ProjectMetrics("Sample.Domain", Layer.Core, 50.0)],
            Counts());

        BaselineRatchet.Evaluate(baseline, Make(coverage: 79.0)).Coverage.Offenders
            .Should().Equal("Sample.Domain: line coverage 79 < baseline 80 (-1 pp)");
    }

    [Fact]
    [Trait("scenario", "US2-AS38")]
    public void Coverage_offenders_are_sorted_and_actual_is_the_largest_drop()
    {
        var baseline = new Baseline(
            "c", "t",
            [new ProjectMetrics("Sample.Zeta", Layer.Core, 80.0), new ProjectMetrics("Sample.Alpha", Layer.Core, 80.0)],
            Counts());
        var current = new Baseline(
            "c", "t",
            [new ProjectMetrics("Sample.Zeta", Layer.Core, 79.0), new ProjectMetrics("Sample.Alpha", Layer.Core, 77.0)],
            Counts());

        var result = BaselineRatchet.Evaluate(baseline, current).Coverage;

        result.Offenders.Should().Equal(
            "Sample.Alpha: line coverage 77 < baseline 80 (-3 pp)",
            "Sample.Zeta: line coverage 79 < baseline 80 (-1 pp)");
        result.Actual.Should().Be(3);
    }
}
