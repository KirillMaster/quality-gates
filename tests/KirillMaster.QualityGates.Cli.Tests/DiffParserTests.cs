using FluentAssertions;
using KirillMaster.QualityGates.Cli.Domain.Diff;

namespace KirillMaster.QualityGates.Cli.Tests;

public class DiffParserTests
{
    private static ChangedLines Parse(string fixture) =>
        DiffParser.Parse(File.ReadAllText(Fx.Path("diffs", fixture)));

    [Fact]
    [Trait("scenario", "US2-AS20")]
    public void Hunk_yields_changed_line_set()
    {
        var r = Parse("hunk-basic.diff");
        r.LinesOf("src/A.cs").Should().Equal(11, 12, 13);
    }

    [Fact]
    [Trait("scenario", "US2-AS21")]
    public void Single_line_hunk_and_pure_deletion()
    {
        var r = Parse("hunk-single-and-deletion.diff");
        r.LinesOf("src/A.cs").Should().Equal(5);
    }

    [Fact]
    [Trait("scenario", "US2-AS22")]
    public void Renamed_reported_under_new_path_and_deleted_has_no_lines()
    {
        var r = Parse("rename-delete.diff");
        r.LinesOf("src/New.cs").Should().Equal(3);
        r.LinesOf("src/Old.cs").Should().BeEmpty();
        r.LinesOf("src/Gone.cs").Should().BeEmpty();
    }

    [Theory]
    [Trait("scenario", "US2-AS23")]
    [InlineData("new-file-lf.diff")]
    [InlineData("new-file-crlf.diff")]
    public void New_file_yields_all_lines_for_lf_and_crlf(string fixture)
    {
        Parse(fixture).LinesOf("src/N.cs").Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    [Trait("scenario", "US2-AS23")]
    public void Crlf_fixture_really_has_crlf() =>
        File.ReadAllText(Fx.Path("diffs", "new-file-crlf.diff")).Should().Contain("\r\n");

    [Fact]
    [Trait("scenario", "US2-AS24")]
    public void Only_csharp_files_are_considered()
    {
        var r = Parse("mixed-extensions.diff");
        r.Files.Should().BeEquivalentTo(["src/A.cs"]);
    }
}
