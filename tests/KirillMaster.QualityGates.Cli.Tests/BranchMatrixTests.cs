using FluentAssertions;
using KirillMaster.QualityGates.Cli.Domain;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class BranchMatrixTests
{
    [Theory]
    [Trait("scenario", "US3-AS1")]
    [InlineData("feature/x", "develop")]
    [InlineData("feature/RES-14256/a", "origin/develop")]
    [InlineData("bugfix/y", "develop")]
    [InlineData("bugfix/y", "origin/develop")]
    public void Feature_and_bugfix_into_develop_are_blocking(string source, string target) =>
        BranchMatrix.Resolve(source, target).Should().Be(Policy.Blocking);

    [Theory]
    [Trait("scenario", "US3-AS2")]
    [InlineData("hotfix/x", "master")]
    [InlineData("hotfix/x", "origin/master")]
    [InlineData("develop", "release/2.17")]
    [InlineData("develop", "origin/release/2.17")]
    [InlineData("release/2.17", "master")]
    [InlineData("release/2.17", "origin/master")]
    public void Hotfix_and_release_flows_skip(string source, string target) =>
        BranchMatrix.Resolve(source, target).Should().Be(Policy.Skip);

    [Theory]
    [Trait("scenario", "US3-AS3")]
    [InlineData("spike/x", "develop")]
    [InlineData("feature/x", "master")]
    [InlineData("feature/x", "release/2.17")]
    [InlineData("develop", "develop")]
    [InlineData("feature/x", "staging")]
    public void Unknown_combinations_are_report_only(string source, string target) =>
        BranchMatrix.Resolve(source, target).Should().Be(Policy.ReportOnly);

    [Theory]
    [Trait("scenario", "US3-AS4")]
    [InlineData("", "develop")]
    [InlineData("feature/x", "")]
    [InlineData("", "")]
    [InlineData(null, "develop")]
    [InlineData("feature/x", null)]
    [InlineData(null, null)]
    public void Empty_or_null_names_are_report_only(string? source, string? target) =>
        BranchMatrix.Resolve(source, target).Should().Be(Policy.ReportOnly);

    [Theory]
    [Trait("scenario", "US3-AS5")]
    [InlineData("features/x", "develop")]
    [InlineData("feature", "develop")]
    [InlineData("feature/", "develop")]
    [InlineData("Feature/x", "develop")]
    public void Prefixes_match_whole_segments_case_sensitively(string source, string target) =>
        BranchMatrix.Resolve(source, target).Should().Be(Policy.ReportOnly);

    [Fact]
    [Trait("scenario", "US3-AS6")]
    public void Matrix_is_table_driven()
    {
        BranchMatrix.Resolve("spike/x", "develop").Should().Be(Policy.ReportOnly);

        var added = BranchMatrix.DefaultRules.Append(new BranchRule("spike/", "develop", Policy.Blocking)).ToList();
        BranchMatrix.Resolve("spike/x", "develop", added).Should().Be(Policy.Blocking);

        var removed = BranchMatrix.DefaultRules.Where(r => r.SourcePattern != "feature/").ToList();
        BranchMatrix.Resolve("feature/x", "develop", removed).Should().Be(Policy.ReportOnly);

        BranchMatrix.Resolve("feature/x", "develop", []).Should().Be(Policy.ReportOnly);
    }

    [Fact]
    [Trait("scenario", "US3-AS6")]
    public void BranchMatrix_lives_in_Domain_without_outer_layer_dependencies()
    {
        var type = typeof(BranchMatrix);
        type.Namespace.Should().Be("KirillMaster.QualityGates.Cli.Domain");
        type.Assembly.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain(n => n!.Contains("Application") || n.Contains("Adapters") || n.Contains("Cli"));
    }
}
