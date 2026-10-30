using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Sarif;

namespace KirillMaster.QualityGates.Cli.Tests;

public class SarifReaderTests
{
    [Fact]
    [Trait("scenario", "US2-AS37")]
    public void Sarif_files_are_aggregated_per_project_and_rule()
    {
        var result = SarifReader.Aggregate(
        [
            Fx.Path("sarif", "Sample.Domain.net9.0.sarif"),
            Fx.Path("sarif", "Sample.Adapters.net9.0.sarif")
        ]);

        result.Keys.Should().BeEquivalentTo("Sample.Domain", "Sample.Adapters");
        result["Sample.Domain"].Should().BeEquivalentTo(new Dictionary<string, int> { ["S1481"] = 2, ["VSTHRD002"] = 1 });
        result["Sample.Adapters"].Should().BeEquivalentTo(new Dictionary<string, int> { ["S1481"] = 1 });
    }

    [Fact]
    [Trait("scenario", "US2-AS37")]
    public void Multi_target_files_of_one_project_do_not_double_count()
    {
        var dir = Directory.CreateTempSubdirectory("qg-sarif-").FullName;
        try
        {
            File.Copy(Fx.Path("sarif", "Sample.Domain.net9.0.sarif"), Path.Combine(dir, "Sample.Domain.net9.0.sarif"));
            File.Copy(Fx.Path("sarif", "Sample.Domain.net9.0.sarif"), Path.Combine(dir, "Sample.Domain.netstandard2.0.sarif"));

            var result = SarifReader.Aggregate(Directory.GetFiles(dir));

            result.Keys.Should().Equal("Sample.Domain");
            result["Sample.Domain"]["S1481"].Should().Be(2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [Trait("scenario", "US2-AS37")]
    [InlineData("obj/quality/Sample.Domain.net9.0.sarif", "Sample.Domain")]
    [InlineData("Sample.Adapters.netstandard2.0.sarif", "Sample.Adapters")]
    [InlineData("Sample.Legacy.net48.sarif", "Sample.Legacy")]
    [InlineData("Sample.Plain.sarif", "Sample.Plain")]
    public void Project_name_is_the_file_name_without_target_framework(string path, string expected) =>
        SarifReader.ProjectName(path).Should().Be(expected);

    [Fact]
    [Trait("scenario", "US2-AS37")]
    public void Results_expose_location_and_suppression_state()
    {
        var results = SarifReader.ReadResults(Fx.Path("sarif", "Sample.Domain.net9.0.sarif"));

        results.Should().HaveCount(5);
        results[0].RuleId.Should().Be("S1481");
        results[0].Level.Should().Be("warning");
        results[0].Suppressed.Should().BeFalse();
        results[3].Suppressed.Should().BeTrue();
        results[4].Level.Should().Be("note");
    }
}
