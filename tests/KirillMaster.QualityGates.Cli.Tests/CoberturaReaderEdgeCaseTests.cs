using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Coverage;

namespace KirillMaster.QualityGates.Cli.Tests;

public class CoberturaReaderEdgeCaseTests
{
    private static Domain.Coverage.CoverageReport ReadXml(string xml, string root)
    {
        var file = Path.Combine(Path.GetTempPath(), "qg-cob-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, xml);
        try
        {
            return CoberturaReader.Read(file, root);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static Domain.Coverage.CoverageReport ReadBase(string xml, string root, string? reportRoot)
    {
        var file = Path.Combine(Path.GetTempPath(), "qg-cob-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, xml);
        try
        {
            return CoberturaReader.Read(file, root, reportRoot, detectRoot: true);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_base_report_from_another_root_maps_moved_and_deleted_files_to_repo_relative_paths()
    {
        var repo = Path.Combine(Path.GetTempPath(), "qg-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, "src", "Kept.cs"), "class Kept { }");
        const string Xml = """
            <coverage><packages><package><classes>
              <class filename="/agent7/ws/base/src/Kept.cs"><lines><line number="1" hits="1" /></lines></class>
              <class filename="/agent7/ws/base/src/Deleted.cs"><lines><line number="2" hits="3" /></lines></class>
            </classes></package></packages></coverage>
            """;
        try
        {
            var detected = ReadBase(Xml, repo, null);
            var explicitRoot = ReadBase(Xml, repo, "/agent7/ws/base/");

            detected.Files.Should().BeEquivalentTo(["src/Kept.cs", "src/Deleted.cs"]);
            explicitRoot.Files.Should().BeEquivalentTo(["src/Kept.cs", "src/Deleted.cs"]);
            detected.TryGetHits("src/Deleted.cs", 2, out var hits).Should().BeTrue();
            hits.Should().Be(3);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void A_base_worktree_nested_inside_the_checkout_maps_to_repo_relative_paths()
    {
        var repo = Path.Combine(Path.GetTempPath(), "qg-nest-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        Directory.CreateDirectory(Path.Combine(repo, "qg-base", "src"));
        File.WriteAllText(Path.Combine(repo, "src", "A.cs"), "class A { }");
        File.WriteAllText(Path.Combine(repo, "qg-base", "src", "A.cs"), "class A { }");
        var xml = $"""
            <coverage><packages><package><classes>
              <class filename="{repo}/qg-base/src/A.cs"><lines><line number="1" hits="1" /></lines></class>
              <class filename="{repo}/qg-base/src/Gone.cs"><lines><line number="1" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """;
        try
        {
            ReadBase(xml, repo, null).Files.Should().BeEquivalentTo(["src/A.cs", "src/Gone.cs"]);
            ReadBase(xml, repo, repo + "/qg-base").Files.Should().BeEquivalentTo(["src/A.cs", "src/Gone.cs"]);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void A_base_report_with_relative_filenames_and_an_absolute_source_from_another_root_is_re_anchored()
    {
        var repo = Path.Combine(Path.GetTempPath(), "qg-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, "src", "Svc"));
        File.WriteAllText(Path.Combine(repo, "src", "Svc", "A.cs"), "class A { }");
        const string Xml = """
            <coverage><sources><source>/agent7/ws/base/src/Svc/</source></sources><packages><package><classes>
              <class filename="A.cs"><lines><line number="1" hits="1" /></lines></class>
              <class filename="Gone.cs"><lines><line number="1" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """;
        try
        {
            ReadBase(Xml, repo, null).Files.Should().BeEquivalentTo(["src/Svc/A.cs", "src/Svc/Gone.cs"]);
            ReadBase(Xml, repo, "/agent7/ws/base").Files.Should().BeEquivalentTo(["src/Svc/A.cs", "src/Svc/Gone.cs"]);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS26")]
    public void Dot_slash_relative_filename_is_normalized_to_plain_relative_path()
    {
        var r = ReadXml("""
            <coverage><packages><package><classes>
              <class filename="./src/A.cs"><lines><line number="1" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """, "/nonexistent-root");

        r.Files.Should().BeEquivalentTo(["src/A.cs"]);
    }

    [Fact]
    public void Relative_filename_is_anchored_to_a_source_under_the_root()
    {
        var r = ReadXml("""
            <coverage><sources><source>/src/backend/Svc/</source></sources><packages><package><classes>
              <class filename="Core/A.cs"><lines><line number="1" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """, "/src");

        r.Files.Should().BeEquivalentTo(["backend/Svc/Core/A.cs"]);
    }

    [Fact]
    public void Relative_filename_is_anchored_to_the_source_it_exists_under()
    {
        var root = Path.Combine(Path.GetTempPath(), "qg-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "b", "Core"));
        File.WriteAllText(Path.Combine(root, "b", "Core", "A.cs"), "");
        try
        {
            var slashRoot = root.Replace('\\', '/');
            var r = ReadXml($"""
                <coverage><sources><source>{slashRoot}/a/</source><source>{slashRoot}/b/</source></sources><packages><package><classes>
                  <class filename="Core/A.cs"><lines><line number="1" hits="1" /></lines></class>
                </classes></package></packages></coverage>
                """, root);

            r.Files.Should().BeEquivalentTo(["b/Core/A.cs"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("./", "Core/A.cs")]
    [InlineData(".", "Core/A.cs")]
    [InlineData("./backend/Svc/", "backend/Svc/Core/A.cs")]
    public void Relative_source_is_normalized_before_anchoring(string source, string expected)
    {
        var r = ReadXml($"""
            <coverage><sources><source>{source}</source></sources><packages><package><classes>
              <class filename="Core/A.cs"><lines><line number="1" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """, "/src");

        r.Files.Should().BeEquivalentTo([expected]);
    }

    [Fact]
    public void Relative_filename_with_source_outside_the_root_is_kept_as_is()
    {
        var r = ReadXml("""
            <coverage><sources><source>/elsewhere/Svc/</source></sources><packages><package><classes>
              <class filename="Core/A.cs"><lines><line number="1" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """, "/src");

        r.Files.Should().BeEquivalentTo(["Core/A.cs"]);
    }

    [Fact]
    [Trait("scenario", "US2-AS26")]
    public void Single_character_relative_filename_is_kept_as_is()
    {
        var r = ReadXml("""
            <coverage><packages><package><classes>
              <class filename="a"><lines><line number="1" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """, "/nonexistent-root");

        r.Files.Should().BeEquivalentTo(["a"]);
    }

    [Fact]
    [Trait("scenario", "US2-AS26")]
    public void Class_without_filename_is_skipped_and_others_still_read()
    {
        var r = ReadXml("""
            <coverage><packages><package><classes>
              <class><lines><line number="1" hits="1" /></lines></class>
              <class filename="src/A.cs"><lines><line number="2" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """, "/nonexistent-root");

        r.Files.Should().BeEquivalentTo(["src/A.cs"]);
    }

    [Fact]
    [Trait("scenario", "US2-AS36")]
    public void Class_entry_without_line_elements_still_registers_its_file()
    {
        var r = ReadXml("""
            <coverage><packages><package><classes>
              <class filename="src/Empty.cs"></class>
            </classes></package></packages></coverage>
            """, "/nonexistent-root");

        r.HasFile("src/Empty.cs").Should().BeTrue();
        r.TryGetHits("src/Empty.cs", 1, out _).Should().BeFalse();
    }

    [Fact]
    [Trait("scenario", "US2-AS26")]
    public void Absolute_path_outside_root_drops_only_the_drive_segment()
    {
        var r = ReadXml("""
            <coverage><packages><package><classes>
              <class filename="C:\elsewhere\x\Missing.cs"><lines><line number="1" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """, @"C:\ws\repo");

        r.Files.Should().BeEquivalentTo(["elsewhere/x/Missing.cs"]);
    }

    [Fact]
    [Trait("scenario", "US2-AS26")]
    public void Root_prefix_must_match_a_whole_directory_name()
    {
        var r = ReadXml("""
            <coverage><packages><package><classes>
              <class filename="C:\ws\repository\src\A.cs"><lines><line number="1" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """, @"C:\ws\repo");

        r.Files.Should().BeEquivalentTo(["ws/repository/src/A.cs"]);
    }
}
