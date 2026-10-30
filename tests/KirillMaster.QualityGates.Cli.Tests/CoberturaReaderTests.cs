using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Coverage;

namespace KirillMaster.QualityGates.Cli.Tests;

public class CoberturaReaderTests
{
    [Fact]
    [Trait("scenario", "US2-AS25")]
    public void Merge_counts_line_covered_when_any_report_covers_it()
    {
        var r = CoberturaReader.ReadMerged(
            [Fx.Path("cobertura", "unit.xml"), Fx.Path("cobertura", "integration.xml")], "/repo");

        r.TryGetHits("src/A.cs", 7, out var hits7).Should().BeTrue();
        hits7.Should().Be(3);
    }

    [Fact]
    [Trait("scenario", "US2-AS25")]
    public void Merge_keeps_unit_hits_for_line_only_in_unit_report_regardless_of_order()
    {
        var r = CoberturaReader.ReadMerged(
            [Fx.Path("cobertura", "integration.xml"), Fx.Path("cobertura", "unit.xml")], "/repo");

        r.TryGetHits("src/A.cs", 8, out var hits8).Should().BeTrue();
        hits8.Should().Be(2);
        r.TryGetHits("src/A.cs", 7, out var hits7).Should().BeTrue();
        hits7.Should().Be(3);
    }

    [Fact]
    [Trait("scenario", "US2-AS26")]
    public void Absolute_agent_paths_are_normalized_repo_relative()
    {
        var root = Path.Combine(Path.GetTempPath(), "qg-cob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Deep"));
        File.WriteAllText(Path.Combine(root, "src", "Deep", "B.cs"), "class B {}");
        try
        {
            var r = CoberturaReader.Read(Fx.Path("cobertura", "abs-paths.xml"), root);

            r.Files.Should().BeEquivalentTo(["src/A.cs", "src/Deep/B.cs"]);
            r.Files.Should().OnlyContain(p => !p.Contains(':') && !p.Contains('\\') && !p.Contains("agent"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS26")]
    public void Absolute_paths_under_repo_root_are_made_relative()
    {
        var file = Path.Combine(Path.GetTempPath(), "qg-cob-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, """
            <coverage><packages><package><classes>
              <class filename="C:\ws\repo\src\A.cs"><lines><line number="1" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """);
        try
        {
            CoberturaReader.Read(file, @"C:\ws\repo").Files.Should().BeEquivalentTo(["src/A.cs"]);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
