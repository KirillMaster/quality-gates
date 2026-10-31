using FluentAssertions;
using KirillMaster.QualityGates.Cli.Domain.Diff;

namespace KirillMaster.QualityGates.Cli.Tests;

public class DiffParserEdgeCaseTests
{
    private const string Header = "diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs";

    private static ChangedLines Parse(params string[] lines) => DiffParser.Parse(string.Join('\n', lines));

    [Fact]
    [Trait("scenario", "US2-AS21")]
    public void Lines_after_a_closed_hunk_are_not_counted()
    {
        var r = Parse(Header, "@@ -1,1 +1,1 @@", "-old", "+new", "+stray");

        r.LinesOf("src/A.cs").Should().Equal(1);
    }

    [Fact]
    [Trait("scenario", "US2-AS20")]
    public void Added_line_whose_content_starts_with_plus_plus_space_stays_inside_the_hunk()
    {
        var r = Parse(Header, "@@ -0,0 +1,2 @@", "+++ b/src/B.cs", "+plain");

        r.LinesOf("src/A.cs").Should().Equal(1, 2);
        r.Files.Should().BeEquivalentTo(["src/A.cs"]);
    }

    [Fact]
    [Trait("scenario", "US2-AS20")]
    public void Explicit_new_count_bounds_the_hunk_and_later_plus_lines_are_ignored()
    {
        var r = Parse(Header, "@@ -0,0 +1,2 @@", "+a", "+b", "+stray");

        r.LinesOf("src/A.cs").Should().Equal(1, 2);
    }

    [Fact]
    [Trait("scenario", "US2-AS21")]
    public void No_newline_marker_after_a_deleted_last_line_adds_nothing()
    {
        var r = Parse(Header, "@@ -1 +0,0 @@", "-old", "\\ No newline at end of file");

        r.LinesOf("src/A.cs").Should().BeEmpty();
    }

    [Fact]
    [Trait("scenario", "US2-AS21")]
    public void Omitted_hunk_count_defaults_to_one()
    {
        var r = Parse(Header, "@@ -5 +7 @@", "+x");

        r.LinesOf("src/A.cs").Should().Equal(7);
    }

    [Fact]
    [Trait("scenario", "US2-AS24")]
    public void File_header_without_b_prefix_keeps_its_path_unchanged()
    {
        var r = Parse("diff --git src/A.cs src/A.cs", "+++ src/A.cs", "@@ -0,0 +1 @@", "+x");

        r.Files.Should().BeEquivalentTo(["src/A.cs"]);
        r.LinesOf("src/A.cs").Should().Equal(1);
    }
}
