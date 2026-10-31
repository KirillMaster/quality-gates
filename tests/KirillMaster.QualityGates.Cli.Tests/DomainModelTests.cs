using System.Reflection;
using FluentAssertions;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class DomainModelTests
{
    [Fact]
    [Trait("scenario", "US2-AS4")]
    public void Default_config_equals_agreed_thresholds()
    {
        var d = GatesConfig.Default;
        d.DiffCoverage.Core.Should().Be(90);
        d.DiffCoverage.Adapter.Should().Be(80);
        d.Mutation.CoreThreshold.Should().Be(80);
        d.Mutation.DiffBudgetMinutes.Should().Be(30);
        d.Mutation.NightlyBudgetMinutes.Should().Be(240);
        d.IntegrationTestsEnabled.Should().BeTrue();
    }

    [Fact]
    [Trait("scenario", "US2-AS5")]
    public void Records_are_immutable_values()
    {
        var config = GatesConfig.Default with { Service = "a" };
        var changed = config with { Service = "b" };
        config.Service.Should().Be("a");
        changed.Service.Should().Be("b");
        (config with { Service = "a" }).Should().Be(config);

        var metrics = new[] { new ProjectMetrics("P", Layer.Core, 50) };
        var warnings = new Dictionary<string, IReadOnlyDictionary<string, int>>();
        var baseline = new Baseline("c1", "2026-01-01T00:00:00Z", metrics, warnings);
        (baseline with { Commit = "c2" }).Commit.Should().Be("c2");
        baseline.Commit.Should().Be("c1");
        (baseline with { }).Should().Be(baseline);

        var result = new GateResult(GateId.DiffCoverage, Verdict.Pass, ResultLayer.Core, 91, 90);
        (result with { Verdict = Verdict.Fail }).Verdict.Should().Be(Verdict.Fail);
        result.Verdict.Should().Be(Verdict.Pass);
        (result with { }).Should().Be(result);
    }

    [Fact]
    [Trait("scenario", "US2-AS5")]
    public void Model_types_have_no_mutable_setters()
    {
        var types = typeof(GatesConfig).Assembly.GetTypes()
            .Where(t => t.Namespace == "KirillMaster.QualityGates.Cli.Domain.Model" && t.IsClass);
        foreach (var p in types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
        {
            var setter = p.SetMethod;
            (setter is null || setter.ReturnParameter.GetRequiredCustomModifiers()
                .Any(m => m.Name == "IsExternalInit")).Should().BeTrue($"{p.DeclaringType!.Name}.{p.Name}");
        }
    }
}
