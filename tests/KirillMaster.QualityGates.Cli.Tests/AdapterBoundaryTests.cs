using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Grandfather;
using KirillMaster.QualityGates.Cli.Adapters.Sarif;

namespace KirillMaster.QualityGates.Cli.Tests;

public class AdapterBoundaryTests
{
    private static string Sarif(params string[] results) =>
        "{\"runs\":[{\"results\":[" + string.Join(",", results) + "]}]}";

    private static string Result(string rule, string level, bool suppressed = false) =>
        "{\"ruleId\":\"" + rule + "\",\"level\":\"" + level + "\""
        + (suppressed ? ",\"suppressions\":[{\"kind\":\"inSource\"}]" : string.Empty) + "}";

    private static string WriteFile(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    [Trait("scenario", "US2-AS37")]
    public void Aggregate_counts_only_unsuppressed_warnings_and_errors()
    {
        var dir = Directory.CreateTempSubdirectory("qg-sarif-").FullName;
        try
        {
            var path = WriteFile(dir, "Sample.Domain.net9.0.sarif", Sarif(
                Result("S1481", "warning"),
                Result("S1481", "error"),
                Result("S1481", "note"),
                Result("S1481", "warning", suppressed: true)));

            SarifReader.Aggregate([path])["Sample.Domain"]["S1481"].Should().Be(2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [Trait("scenario", "US2-AS37")]
    [InlineData(true)]
    [InlineData(false)]
    public void Target_frameworks_of_one_project_merge_by_the_largest_count_in_either_order(bool bigFirst)
    {
        var dir = Directory.CreateTempSubdirectory("qg-sarif-").FullName;
        try
        {
            var big = WriteFile(dir, "Sample.Domain.net9.0.sarif", Sarif(
                Result("S1481", "warning"), Result("S1481", "warning"), Result("S1481", "warning")));
            var small = WriteFile(dir, "Sample.Domain.net8.0.sarif", Sarif(Result("S1481", "warning")));

            var inputs = bigFirst ? new[] { big, small } : new[] { small, big };

            SarifReader.Aggregate(inputs)["Sample.Domain"]["S1481"].Should().Be(3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS45")]
    public void Count_sites_counts_only_marker_lines_and_skips_projects_without_sites()
    {
        var repo = Directory.CreateTempSubdirectory("qg-gf-").FullName;
        try
        {
            var directory = GrandfatherFiles.DirectoryOf(repo);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Sample.Adapters" + GrandfatherFiles.Suffix),
                "// header\n"
                + "[assembly: SuppressMessage(\"ApiDesign\", \"RS0030\", Scope = \"member\", Target = \"~M:A.B()\")]\n"
                + "[assembly: SuppressMessage(\"ApiDesign\", \"CA1000\", Target = \"~M:A.C()\")]\n"
                + "[assembly: SuppressMessage(\"ApiDesign\", \"RS0030\", Scope = \"member\", Target = \"~M:A.D()\")]\n");
            File.WriteAllText(Path.Combine(directory, "Sample.Empty" + GrandfatherFiles.Suffix), "// header only\n");

            var counts = GrandfatherFiles.CountSites(repo);

            counts.Should().ContainKey("Sample.Adapters");
            counts["Sample.Adapters"]["RS0030"].Should().Be(2);
            counts.Should().NotContainKey("Sample.Empty");
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS45")]
    public void Existing_projects_are_sorted_and_ignore_files_without_the_suffix()
    {
        var repo = Directory.CreateTempSubdirectory("qg-gf-").FullName;
        try
        {
            var files = new GrandfatherFiles();
            files.Write(repo, "Sample.Zeta", "// z\n");
            files.Write(repo, "Sample.Alpha", "// a\n");
            File.WriteAllText(Path.Combine(GrandfatherFiles.DirectoryOf(repo), "README.md"), "notes");

            files.ExistingProjects(repo).Should().Equal("Sample.Alpha", "Sample.Zeta");
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
}
