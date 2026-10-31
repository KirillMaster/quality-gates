using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Json;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class ConfigBaselineStoreTests
{
    private static string Fixture(string name) => Fx.Path("config", name);

    [Fact]
    [Trait("scenario", "US2-AS12")]
    public void Omitted_thresholds_fall_back_to_defaults()
    {
        var result = new JsonConfigStore().Load(Fixture("gates-minimal.json"));

        result.Error.Should().BeNull();
        result.Config!.Service.Should().Be("sample");
        result.Config.DiffCoverage.Core.Should().Be(90);
        result.Config.DiffCoverage.Adapter.Should().Be(80);
        result.Config.IntegrationTestsEnabled.Should().BeTrue();
    }

    [Fact]
    [Trait("scenario", "US2-AS13")]
    public void Thresholds_are_read_from_config()
    {
        var result = new JsonConfigStore().Load(Fixture("gates-custom.json"));

        result.Config!.DiffCoverage.Core.Should().Be(95);
        result.Config.DiffCoverage.Adapter.Should().Be(70);
    }

    [Fact]
    [Trait("scenario", "US2-AS14")]
    public void Malformed_config_yields_error_with_field_path_and_no_config()
    {
        var result = new JsonConfigStore().Load(Fixture("gates-bad-type.json"));

        result.Config.Should().BeNull();
        result.Error.Should().NotBeNull();
        result.Error!.FieldPath.Should().Be("diffCoverage.core");
        result.Error.ToString().Should().Contain("diffCoverage.core");
    }

    [Fact]
    [Trait("scenario", "US2-AS14")]
    public void Unparseable_json_yields_error_without_config()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "gates.json");
        File.WriteAllText(path, "{ not json");

        var result = new JsonConfigStore().Load(path);

        result.Config.Should().BeNull();
        result.Error.Should().NotBeNull();
    }

    [Fact]
    [Trait("scenario", "US2-AS14")]
    public void Full_config_and_layer_overrides_are_parsed()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "gates.json");
        File.WriteAllText(path, """
            {"service":"s","integrationTestsEnabled":false,
             "mutation":{"coreThreshold":75,"diffBudgetMinutes":10,"nightlyBudgetMinutes":60},
             "layerOverrides":{"Sample.Api":"adapter"},
             "notifications":{"slackChannel":"#c","jiraTechDebtIssue":"RES-1","s3Bucket":"b","s3Prefix":"p"}}
            """);

        var config = new JsonConfigStore().Load(path).Config!;

        config.Service.Should().Be("s");
        config.DiffCoverage.Should().Be(new DiffCoverageThresholds(90, 80));
        config.IntegrationTestsEnabled.Should().BeFalse();
        config.Mutation.Should().Be(new MutationSettings(75, 10, 60));
        config.LayerOverrides["Sample.Api"].Should().Be(Layer.Adapter);
        config.Notifications.Should().Be(new NotificationSettings("#c", "RES-1", "b", "p"));
    }

    [Theory]
    [Trait("scenario", "US2-AS14")]
    [InlineData("""{"service":1}""", "service")]
    [InlineData("""{"diffCoverage":5}""", "diffCoverage")]
    [InlineData("""{"mutation":{"diffBudgetMinutes":"x"}}""", "mutation.diffBudgetMinutes")]
    [InlineData("""{"integrationTestsEnabled":"yes"}""", "integrationTestsEnabled")]
    [InlineData("""{"layerOverrides":{"A":"bogus"}}""", "layerOverrides.A")]
    [InlineData("""{"notifications":{"slackChannel":3}}""", "notifications.slackChannel")]
    [InlineData("""[1]""", "$")]
    public void Wrong_types_report_their_field_path(string json, string expectedPath)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "gates.json");
        File.WriteAllText(path, json);

        var result = new JsonConfigStore().Load(path);

        result.Config.Should().BeNull();
        result.Error!.FieldPath.Should().Be(expectedPath);
    }

    [Fact]
    [Trait("scenario", "US2-AS14")]
    public void Missing_config_file_is_a_config_error()
    {
        var result = new JsonConfigStore().Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "gates.json"));

        result.Config.Should().BeNull();
        result.Error.Should().NotBeNull();
    }

    [Fact]
    [Trait("scenario", "US2-AS15")]
    public void Baseline_round_trips_through_camel_case_json()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "quality", "baseline.json");
        var original = new Baseline(
            "abc123",
            "2026-10-21T00:00:00Z",
            [
                new ProjectMetrics("Sample.Domain", Layer.Core, 91.5, 80.0, 77.0),
                new ProjectMetrics("Sample.Adapters", Layer.Adapter, 70.0)
            ],
            new Dictionary<string, IReadOnlyDictionary<string, int>>
            {
                ["Sample.Domain"] = new Dictionary<string, int> { ["S1481"] = 3 }
            });
        var store = new JsonBaselineStore();

        store.Save(path, original);
        var loaded = store.Load(path);

        loaded.Should().BeEquivalentTo(original);
        var text = File.ReadAllText(path);
        text.Should().Contain("\"measuredAt\"").And.Contain("\"projects\"").And.Contain("\"lineCoverage\"");
        text.Should().NotContain("\"MeasuredAt\"").And.NotContain("\"LineCoverage\"");
        text.Split('\n').Length.Should().BeGreaterThan(3, "the baseline file is written indented for review diffs");
    }

    [Fact]
    [Trait("scenario", "US2-AS16")]
    public void Absent_baseline_loads_as_null_without_exception()
    {
        using var dir = new TempDir();
        var store = new JsonBaselineStore();
        var path = Path.Combine(dir.Path, "quality", "baseline.json");

        var act = () => store.Load(path);

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Theory]
    [Trait("scenario", "US2-AS14")]
    [InlineData("""{"diffCoverage":{"adapter":"x"}}""", "diffCoverage.adapter", "expected a number")]
    [InlineData("""{"mutation":{"coreThreshold":"x"}}""", "mutation.coreThreshold", "expected a number")]
    [InlineData("""{"mutation":{"nightlyBudgetMinutes":"x"}}""", "mutation.nightlyBudgetMinutes", "expected a number")]
    [InlineData("""{"notifications":{"jiraTechDebtIssue":3}}""", "notifications.jiraTechDebtIssue", "expected a string")]
    [InlineData("""{"notifications":{"s3Bucket":3}}""", "notifications.s3Bucket", "expected a string")]
    [InlineData("""{"notifications":{"s3Prefix":3}}""", "notifications.s3Prefix", "expected a string")]
    [InlineData("""{"integrationTestsEnabled":"yes"}""", "integrationTestsEnabled", "expected a boolean")]
    [InlineData("""{"layerOverrides":{"A":"bogus"}}""", "layerOverrides.A", "unknown layer")]
    [InlineData("""{"layerOverrides":{"A":1}}""", "layerOverrides.A", "expected string")]
    [InlineData("""{"diffCoverage":5}""", "diffCoverage", "expected object")]
    [InlineData("""{"mutation":5}""", "mutation", "expected object")]
    [InlineData("""{"layerOverrides":5}""", "layerOverrides", "expected object")]
    [InlineData("""{"notifications":5}""", "notifications", "expected object")]
    [InlineData("""[1]""", "$", "expected object")]
    [InlineData("""{"service":1}""", "service", "expected a string")]
    public void Wrong_types_report_path_and_reason(string json, string expectedPath, string expectedReason)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "gates.json");
        File.WriteAllText(path, json);

        var result = new JsonConfigStore().Load(path);

        result.Config.Should().BeNull();
        result.Error!.FieldPath.Should().Be(expectedPath);
        result.Error.Message.Should().Contain(expectedReason);
    }

    [Fact]
    [Trait("scenario", "US2-AS12")]
    public void Absent_service_defaults_to_empty_string()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "gates.json");
        File.WriteAllText(path, """{"integrationTestsEnabled":true}""");

        var result = new JsonConfigStore().Load(path);

        result.Error.Should().BeNull();
        result.Config!.Service.Should().BeEmpty();
    }

    [Fact]
    [Trait("scenario", "US2-AS14")]
    public void Invalid_json_reports_root_path_and_reason()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "gates.json");
        File.WriteAllText(path, "{ not json");

        var result = new JsonConfigStore().Load(path);

        result.Error!.FieldPath.Should().Be("$");
        result.Error.Message.Should().StartWith("invalid JSON: ");
    }

    [Fact]
    [Trait("scenario", "US2-AS14")]
    public void Missing_file_reports_root_path_and_names_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "gates.json");

        var result = new JsonConfigStore().Load(path);

        result.Error!.FieldPath.Should().Be("$");
        result.Error.Message.Should().StartWith($"cannot read '{path}': ");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }
}
