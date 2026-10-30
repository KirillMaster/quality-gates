using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Grandfather;
using KirillMaster.QualityGates.Cli.Adapters.Sarif;

namespace KirillMaster.QualityGates.Cli.Tests;

public sealed class AdapterEdgeCaseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("qg-edge-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Sarif(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Grandfather_files_without_directory_report_nothing_and_delete_is_a_noop()
    {
        var files = new GrandfatherFiles();

        files.ExistingProjects(_dir).Should().BeEmpty();
        GrandfatherFiles.CountSites(_dir).Should().BeEmpty();
        var act = () => files.Delete(_dir, "Nope");
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Grandfather_files_list_sorted_projects_and_count_only_marker_lines()
    {
        var files = new GrandfatherFiles();
        files.Write(_dir, "B.Proj", "[assembly: SuppressMessage(\"ApiDesign\", \"RS0030\", \"x\")]\n// other\n[assembly: SuppressMessage(\"ApiDesign\", \"RS0030\", \"y\")]\n");
        files.Write(_dir, "A.Proj", "[assembly: SuppressMessage(\"ApiDesign\", \"RS0030\", \"x\")]\n");
        files.Write(_dir, "C.Proj", "[assembly: SuppressMessage(\"Other\", \"CA1000\", \"x\")]\n");
        File.WriteAllText(Path.Combine(GrandfatherFiles.DirectoryOf(_dir), "ignored.txt"), "x");

        files.ExistingProjects(_dir).Should().Equal("A.Proj", "B.Proj", "C.Proj");
        var counts = GrandfatherFiles.CountSites(_dir);
        counts.Keys.Should().BeEquivalentTo("A.Proj", "B.Proj");
        counts["A.Proj"]["RS0030"].Should().Be(1);
        counts["B.Proj"]["RS0030"].Should().Be(2);
        File.ReadAllText(Path.Combine(GrandfatherFiles.DirectoryOf(_dir), "A.Proj" + GrandfatherFiles.Suffix)).Should().Contain("RS0030");

        files.Delete(_dir, "B.Proj");
        files.ExistingProjects(_dir).Should().Equal("A.Proj", "C.Proj");
    }

    [Fact]
    [Trait("scenario", "US2-AS37")]
    public void Read_results_applies_defaults_for_sparse_results()
    {
        var path = Sarif(
            "s.sarif",
            """
            {"runs":[{"results":[{}, {"ruleId":"X1","level":"error","suppressions":[],"locations":[]},
            {"ruleId":"X2","suppressions":[{"kind":"inSource"}],"locations":[{"physicalLocation":{"artifactLocation":{"uri":"a.cs"}}}]},
            {"ruleId":"X3","locations":[{"physicalLocation":{"region":{"startLine":7}}}]},
            {"ruleId":"X4","locations":[{"physicalLocation":{"artifactLocation":{"uri":"b.cs"},"region":{"startColumn":3}}}]}]},{"tool":{}}]}
            """);

        var r = SarifReader.ReadResults(path);

        r.Should().HaveCount(5);
        r[0].Should().Be(new SarifResult(string.Empty, "warning", null, 0, 0, false));
        r[1].Should().Be(new SarifResult("X1", "error", null, 0, 0, false));
        r[2].Should().Be(new SarifResult("X2", "warning", "a.cs", 0, 0, true));
        r[3].Should().Be(new SarifResult("X3", "warning", null, 7, 0, false));
        r[4].Should().Be(new SarifResult("X4", "warning", "b.cs", 0, 3, false));
    }

    [Fact]
    [Trait("scenario", "US2-AS37")]
    public void Read_results_of_a_document_without_runs_is_empty()
    {
        SarifReader.ReadResults(Sarif("e.sarif", "{}")).Should().BeEmpty();
    }

    [Fact]
    [Trait("scenario", "US2-AS37")]
    public void Null_rule_and_level_values_fall_back_to_defaults()
    {
        var path = Sarif("n.sarif", """{"runs":[{"results":[{"ruleId":null,"level":null}]}]}""");

        SarifReader.ReadResults(path).Should().ContainSingle().Which.Should().Be(new SarifResult(string.Empty, "warning", null, 0, 0, false));
    }

    [Fact]
    [Trait("scenario", "US2-AS37")]
    public void Aggregate_skips_suppressed_and_non_counted_and_merges_target_frameworks_by_max()
    {
        var a = Sarif(
            "P.net9.0.sarif",
            """{"runs":[{"results":[{"ruleId":"R1","level":"warning"},{"ruleId":"R1","level":"ERROR"},{"ruleId":"R1","level":"note"},{"ruleId":"R1","level":"warning","suppressions":[{"kind":"inSource"}]},{"ruleId":"R2","level":"warning"}]}]}""");
        var b = Sarif(
            "P.netstandard2.0.sarif",
            """{"runs":[{"results":[{"ruleId":"R1","level":"warning"},{"ruleId":"R2","level":"warning"},{"ruleId":"R2","level":"warning"},{"ruleId":"R2","level":"warning"}]}]}""");
        var other = Sarif("A.sarif", """{"runs":[{"results":[{"ruleId":"R9","level":"none"}]}]}""");

        var result = SarifReader.Aggregate([a, b, other]);

        result.Keys.Should().Equal("A", "P");
        result["A"].Should().BeEmpty();
        result["P"]["R1"].Should().Be(2);
        result["P"]["R2"].Should().Be(3);
    }

    [Theory]
    [Trait("scenario", "US2-AS37")]
    [InlineData("warning", true)]
    [InlineData("WARNING", true)]
    [InlineData("Error", true)]
    [InlineData("note", false)]
    [InlineData("none", false)]
    [InlineData("", false)]
    public void Only_warning_and_error_levels_are_counted(string level, bool expected) =>
        SarifReader.IsCounted(level).Should().Be(expected);

    [Theory]
    [Trait("scenario", "US2-AS37")]
    [InlineData("X.sarif", "X")]
    [InlineData("X.SARIF", "X")]
    [InlineData("X.net48.sarif", "X")]
    [InlineData("X.net9.0-windows.sarif", "X")]
    [InlineData("X.netcoreapp3.1.sarif", "X")]
    [InlineData("Net.Core.sarif", "Net.Core")]
    [InlineData("Dir/X.y.sarif", "X.y")]
    public void Project_name_strips_extension_and_framework(string file, string expected) =>
        SarifReader.ProjectName(file).Should().Be(expected);
}
