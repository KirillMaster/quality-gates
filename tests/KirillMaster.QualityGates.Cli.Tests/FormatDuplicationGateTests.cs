using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Quality;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class FormatDuplicationGateTests
{
    [Fact]
    public void Format_fails_on_a_pr_for_any_unformatted_file_in_the_repo()
    {
        var result = FormatGate.Evaluate(["src/B.cs", "src/A.cs"], branchBuild: false);

        result.Gate.Should().Be(GateId.Format);
        result.Verdict.Should().Be(Verdict.Fail);
        result.Offenders.Should().Equal("src/A.cs", "src/B.cs");
    }

    [Fact]
    public void Format_only_warns_on_a_branch_build()
    {
        FormatGate.Evaluate(["src/A.cs"], branchBuild: true).Verdict.Should().Be(Verdict.Warn);
    }

    [Fact]
    public void Format_passes_when_nothing_needs_formatting()
    {
        FormatGate.Evaluate([], branchBuild: false).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void Duplication_passes_and_seeds_when_there_is_no_baseline()
    {
        var block = new DuplicateBlock("aa", "a.cs", 1, "b.cs", 1, 20);

        DuplicationGate.Evaluate([block], null).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void Duplication_fails_only_on_blocks_missing_from_the_baseline()
    {
        var known = new DuplicateBlock("aa", "a.cs", 1, "b.cs", 1, 20);
        var added = new DuplicateBlock("bb", "c.cs", 5, "d.cs", 9, 30);

        var result = DuplicationGate.Evaluate([known, added], new HashSet<string> { "aa" });

        result.Verdict.Should().Be(Verdict.Fail);
        result.Actual.Should().Be(1);
        result.Offenders.Should().Equal("c.cs:5 <-> d.cs:9 (30 lines)");
        DuplicationGate.Evaluate([known], new HashSet<string> { "aa", "gone" }).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void Jscpd_fingerprint_ignores_whitespace_and_line_shifts()
    {
        JscpdScanner.Fingerprint("var a = 1;\n  var b = 2;").Should().Be(JscpdScanner.Fingerprint("var a=1;var b =2;"));
        JscpdScanner.Fingerprint("var a = 1;").Should().NotBe(JscpdScanner.Fingerprint("var a = 2;"));
    }

    [Fact]
    public void Jscpd_report_is_parsed_into_repo_relative_blocks()
    {
        var repo = Path.Combine(Path.GetTempPath(), "qg-repo");
        var json = $$$"""
            {"duplicates":[{"lines":18,"fragment":"x y","firstFile":{"name":"{{{Path.Combine(repo, "src", "A.cs").Replace("\\", "/")}}}","startLoc":{"line":10}},"secondFile":{"name":"src/B.cs","start":4}}]}
            """;

        var block = JscpdScanner.ParseReport(json, repo).Single();

        block.FirstFile.Should().Be("src/A.cs");
        block.FirstStart.Should().Be(10);
        block.SecondFile.Should().Be("src/B.cs");
        block.SecondStart.Should().Be(4);
        block.Lines.Should().Be(18);
    }

    [Fact]
    public void Format_report_paths_become_repo_relative()
    {
        var repo = Path.Combine(Path.GetTempPath(), "qg-repo");
        var json = $$"""[{"FilePath":"{{Path.Combine(repo, "src", "A.cs").Replace("\\", "\\\\")}}","FileChanges":[]}]""";

        DotnetFormatChecker.ParseReport(json, repo).Should().Equal("src/A.cs");
    }

    [Fact]
    public void Duplication_baseline_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), "qg-dup-" + Guid.NewGuid().ToString("N"), "duplication-baseline.json");
        var store = new JsonDuplicationBaselineStore();

        store.Load(path).Should().BeNull();
        store.Save(path, ["aa", "bb"]);

        store.Load(path).Should().BeEquivalentTo(["aa", "bb"]);
    }
}
