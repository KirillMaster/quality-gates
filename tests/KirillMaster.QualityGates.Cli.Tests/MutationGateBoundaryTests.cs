using FluentAssertions;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public sealed class MutationGateBoundaryTests
{
    private const string AdapterFile = "src/Sample.Adapters/Repo.cs";

    private static Layer AdapterOf(string _) => Layer.Adapter;

    private static Layer CoreOf(string _) => Layer.Core;

    private static List<MutantResult> Many(string file, int killed, int survived) =>
        Enumerable.Range(0, killed).Select(i => new MutantResult(file, i + 1, "M", MutantStatus.Killed))
            .Concat(Enumerable.Range(0, survived).Select(i => new MutantResult(file, killed + i + 1, "M", MutantStatus.Survived)))
            .ToList();

    [Theory]
    [Trait("scenario", "US3-AS15")]
    [InlineData(79, 21, Verdict.Warn)]
    [InlineData(7999, 2001, Verdict.Warn)]
    [InlineData(80, 20, Verdict.Pass)]
    [InlineData(8001, 1999, Verdict.Pass)]
    public void Adapter_boundary_at_80_and_just_below(int killed, int survived, Verdict expected)
    {
        var result = MutationDiffGate.Evaluate(Many(AdapterFile, killed, survived), AdapterOf, GatesConfig.Default, Policy.Blocking)
            .Single(r => r.Layer == ResultLayer.Adapter);

        result.Verdict.Should().Be(expected);
        result.Threshold.Should().Be(80);
    }

    [Fact]
    [Trait("scenario", "US3-AS15")]
    public void Adapter_warn_lists_offenders_and_actual_score()
    {
        var result = MutationDiffGate.Evaluate(Many(AdapterFile, 1, 1), AdapterOf, GatesConfig.Default, Policy.ReportOnly)
            .Single(r => r.Layer == ResultLayer.Adapter);

        result.Verdict.Should().Be(Verdict.Warn);
        result.Actual.Should().Be(50);
        result.Offenders.Should().Equal($"{AdapterFile}:2 M");
    }

    [Fact]
    [Trait("scenario", "US3-AS11")]
    public void Core_pass_reports_actual_score_and_has_no_offenders()
    {
        var core = MutationDiffGate.Evaluate(Many("src/Sample.Domain/A.cs", 9, 1), CoreOf, GatesConfig.Default, Policy.Blocking)
            .Single(r => r.Layer == ResultLayer.Core);

        core.Verdict.Should().Be(Verdict.Pass);
        core.Actual.Should().Be(90);
        core.Offenders.Should().BeNullOrEmpty();
    }

    [Fact]
    [Trait("scenario", "US3-AS8")]
    public void No_coverage_only_layer_scores_zero_and_fails_blocking()
    {
        var mutants = new[] { new MutantResult("src/Sample.Domain/A.cs", 4, "Linq", MutantStatus.NoCoverage) };

        var core = MutationDiffGate.Evaluate(mutants, CoreOf, GatesConfig.Default, Policy.Blocking).Single(r => r.Layer == ResultLayer.Core);

        core.Verdict.Should().Be(Verdict.Fail);
        core.Actual.Should().Be(0);
        core.Offenders.Should().Equal("src/Sample.Domain/A.cs:4 Linq");
    }

    [Theory]
    [Trait("scenario", "US3-AS7")]
    [InlineData(MutantStatus.Killed, true, false)]
    [InlineData(MutantStatus.Timeout, true, false)]
    [InlineData(MutantStatus.Survived, false, true)]
    [InlineData(MutantStatus.NoCoverage, false, true)]
    [InlineData(MutantStatus.Ignored, false, false)]
    [InlineData(MutantStatus.CompileError, false, false)]
    [InlineData(MutantStatus.RuntimeError, false, false)]
    [InlineData(MutantStatus.Other, false, false)]
    public void Status_classification_is_exact(MutantStatus status, bool detected, bool undetected)
    {
        var m = new MutantResult("f.cs", 1, "M", status);

        m.IsDetected.Should().Be(detected);
        m.IsUndetected.Should().Be(undetected);
    }

    [Theory]
    [Trait("scenario", "US3-AS19")]
    [InlineData(Policy.Blocking)]
    [InlineData(Policy.ReportOnly)]
    public void Timeout_message_names_the_configured_budget(Policy policy)
    {
        var config = GatesConfig.Default with { Mutation = GatesConfig.Default.Mutation with { DiffBudgetMinutes = 45 } };

        var result = MutationDiffGate.Evaluate([], CoreOf, config, policy, timedOut: true).Should().ContainSingle().Subject;

        result.Verdict.Should().Be(Verdict.Timeout);
        result.Layer.Should().Be(ResultLayer.All);
        result.Message.Should().Be("mutation run exceeded the 45 minute budget; the build is UNSTABLE");
    }

    [Fact]
    [Trait("scenario", "US3-AS13")]
    public void Skip_wins_over_timeout()
    {
        var results = MutationDiffGate.Evaluate([], CoreOf, GatesConfig.Default, Policy.Skip, timedOut: true);

        results.Should().ContainSingle().Which.Verdict.Should().Be(Verdict.Skip);
    }

    [Fact]
    [Trait("scenario", "US3-AS12")]
    public void Results_are_core_then_adapter_and_ignored_mutants_alone_are_not_applicable()
    {
        var mutants = new[] { new MutantResult("f.cs", 1, "M", MutantStatus.Ignored), new MutantResult("f.cs", 2, "M", MutantStatus.CompileError) };

        var results = MutationDiffGate.Evaluate(mutants, CoreOf, GatesConfig.Default, Policy.Blocking);

        results.Select(r => r.Layer).Should().Equal(ResultLayer.Core, ResultLayer.Adapter);
        results.Should().OnlyContain(r => r.Verdict == Verdict.NotApplicable);
    }
}
