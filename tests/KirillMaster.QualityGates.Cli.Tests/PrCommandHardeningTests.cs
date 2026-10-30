using System.Text.Json;
using FluentAssertions;
using KirillMaster.QualityGates.Cli;

namespace KirillMaster.QualityGates.Cli.Tests;

public sealed class PrCommandHardeningTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "qg-s7cli-" + Guid.NewGuid().ToString("N"));

    public PrCommandHardeningTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, true); } catch (IOException) { }
    }

    private (int Exit, string Out, string Err, JsonElement? Report) Run(params string[] extra)
    {
        var outDir = Path.Combine(_repo, "out");
        var args = new List<string> { "pr", "--repo", _repo, "--target", "origin/develop", "--source", "feature/x",
            "--coverage", Path.Combine(_repo, "none", "*.xml"), "--sarif", Path.Combine(_repo, "none", "*.sarif"), "--out", outDir };
        args.AddRange(extra);
        var so = new StringWriter();
        var se = new StringWriter();
        var exit = CliHost.Run([.. args], so, se);
        var rp = Path.Combine(outDir, "quality-report.json");
        JsonElement? report = File.Exists(rp) ? JsonDocument.Parse(File.ReadAllText(rp)).RootElement.Clone() : null;
        return (exit, so.ToString(), se.ToString(), report);
    }

    private static string[] Skipped(JsonElement r) => r.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    [Trait("scenario", "US2-AS54")]
    public void Skip_all_exits_zero_reports_all_groups_and_prints_bypassed_lines()
    {
        var r = Run("--skip", "all");
        r.Exit.Should().Be(0);
        r.Err.Should().BeEmpty();
        Skipped(r.Report!.Value).Should().Equal("archdelta", "baselinegrowth", "branchage", "buildtrend", "complexity", "coupling", "coverage", "deadpublic", "duplication", "fanout", "featurereadme", "flows", "format", "ilgraph", "movedcoverage", "mutation", "prsize", "reports", "static", "testdiff", "toggles");
        r.Out.Should().Contain("StaticRatchet: Skip skip (bypassed)".Replace("Skip", "skip", StringComparison.Ordinal))
            .And.Contain("MutationDiff");
        r.Report.Value.GetProperty("results").GetArrayLength().Should().Be(25);
    }

    [Fact]
    [Trait("scenario", "US2-AS54")]
    public void Bypass_by_alone_implies_skip_all_and_is_recorded()
    {
        var r = Run("--bypass-by", "alice");
        r.Exit.Should().Be(0);
        Skipped(r.Report!.Value).Should().Equal("archdelta", "baselinegrowth", "branchage", "buildtrend", "complexity", "coupling", "coverage", "deadpublic", "duplication", "fanout", "featurereadme", "flows", "format", "ilgraph", "movedcoverage", "mutation", "prsize", "reports", "static", "testdiff", "toggles");
        r.Report.Value.GetProperty("bypassedBy").GetString().Should().Be("alice");
    }

    [Fact]
    [Trait("scenario", "US2-AS54")]
    public void Explicit_groups_are_comma_or_space_separated_case_insensitive_and_bypass_by_does_not_widen_them()
    {
        var r = Run("--skip", "Static, COVERAGE", "--skip", "mutation,format,duplication", "--skip", "baselinegrowth,coupling,prsize,archdelta,complexity,toggles,deadpublic,fanout,reports,featurereadme,flows,testdiff,ilgraph,branchage,buildtrend,movedcoverage", "--bypass-by", "bob");
        r.Exit.Should().Be(0);
        Skipped(r.Report!.Value).Should().Equal("archdelta", "baselinegrowth", "branchage", "buildtrend", "complexity", "coupling", "coverage", "deadpublic", "duplication", "fanout", "featurereadme", "flows", "format", "ilgraph", "movedcoverage", "mutation", "prsize", "reports", "static", "testdiff", "toggles");

        var partial = Run("--skip", "static,coverage,format,duplication,baselinegrowth,coupling,prsize,archdelta,complexity,toggles,deadpublic,fanout,reports,featurereadme,flows,testdiff,ilgraph,branchage,buildtrend,movedcoverage", "--bypass-by", "bob");
        partial.Exit.Should().Be(0);
        Skipped(partial.Report!.Value).Should().Equal("archdelta", "baselinegrowth", "branchage", "buildtrend", "complexity", "coupling", "coverage", "deadpublic", "duplication", "fanout", "featurereadme", "flows", "format", "ilgraph", "movedcoverage", "prsize", "reports", "static", "testdiff", "toggles");
        partial.Report.Value.GetProperty("results").GetArrayLength().Should().Be(24);
        partial.Report.Value.GetProperty("bypassedBy").GetString().Should().Be("bob");
    }

    [Fact]
    [Trait("scenario", "US2-AS54")]
    public void Whitespace_bypass_by_is_treated_as_absent_and_does_not_imply_skip()
    {
        var r = Run("--bypass-by", "   ");
        r.Exit.Should().Be(3);
        r.Report!.Value.TryGetProperty("bypassedBy", out _).Should().BeFalse();
    }

    [Fact]
    [Trait("scenario", "US2-AS54")]
    public void Unknown_skip_group_exits_three_with_message_on_stderr_only()
    {
        var r = Run("--skip", "bogus");
        r.Exit.Should().Be(3);
        r.Err.Should().Contain("unknown --skip group 'bogus' (expected static, coverage, mutation, format, duplication, baselinegrowth, coupling, prsize, archdelta, complexity, toggles, deadpublic, fanout, reports, featurereadme, flows, testdiff, ilgraph, branchage, buildtrend, movedcoverage or all)");
        r.Out.Should().NotContain("bogus");
    }

    [Fact]
    [Trait("scenario", "US2-AS52")]
    public void Missing_inputs_print_error_to_stderr_and_nothing_to_stdout()
    {
        var r = Run();
        r.Exit.Should().Be(3);
        r.Err.Should().Contain("no coverage files were found for:");
        r.Out.Should().BeEmpty();
    }
}
