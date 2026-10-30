using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using KirillMaster.QualityGates.Cli;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

internal static class ReportPrinterProbe
{
    public static void Print(RunReport r, TextWriter o, TextWriter e) => KirillMaster.QualityGates.Cli.Commands.ReportPrinter.Print(r, o, e);
}

public sealed class MutationDiffCliTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "qg-s10-" + Guid.NewGuid().ToString("N"));

    public MutationDiffCliTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "App.Domain"));
        File.WriteAllText(Path.Combine(_repo, "App.Domain", "App.Domain.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_repo, "App.Domain", "A.cs"), "class A {}\n");
        Git("init", "-q", "-b", "develop");
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        Git("checkout", "-q", "-b", "feature/x");
        File.WriteAllText(Path.Combine(_repo, "App.Domain", "A.cs"), "class A { int X; }\n");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-am", "change");
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }

        try { Directory.Delete(_repo, true); } catch (IOException) { }
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    private sealed class FakeRunner(MutationRun? run = null, Exception? error = null) : IMutationRunner
    {
        public int Calls;

        public Task<MutationRun> RunAsync(string repoPath, string targetBranch, CancellationToken cancellationToken = default)
        {
            Calls++;
            return error is null ? Task.FromResult(run ?? MutationRun.Completed([])) : throw error;
        }
    }

    private (int Exit, string Out, string Err, JsonElement? Verdict) Run(IMutationRunner runner, params string[] extra)
    {
        var outDir = Path.Combine(_repo, "out");
        var args = new List<string> { "mutation-diff", "--repo", _repo, "--target", "develop", "--source", "feature/x", "--out", outDir };
        args.AddRange(extra);
        var so = new StringWriter();
        var se = new StringWriter();
        var exit = CliHost.Run([.. args], so, se, null, runner);
        var path = Path.Combine(outDir, "mutation-verdict.json");
        return (exit, so.ToString(), se.ToString(), File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone() : null);
    }

    private static MutationRun Killed(int killed, int survived)
    {
        var list = new List<MutantResult>();
        for (var i = 0; i < killed; i++)
        {
            list.Add(new MutantResult("App.Domain/A.cs", i + 1, "Arithmetic", MutantStatus.Killed));
        }

        for (var i = 0; i < survived; i++)
        {
            list.Add(new MutantResult("App.Domain/A.cs", 50 + i, "Boolean", MutantStatus.Survived));
        }

        return MutationRun.Completed(list);
    }

    [Fact]
    [Trait("scenario", "US3-AS27")]
    public void Mutation_diff_is_wired_and_writes_the_verdict()
    {
        var r = Run(new FakeRunner(Killed(9, 1)));
        r.Err.Should().NotContain("is not implemented yet");
        r.Verdict.Should().NotBeNull();
        r.Exit.Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US3-AS28")]
    public void Handler_override_receives_mutation_diff_and_no_runner_is_invoked()
    {
        var runner = new FakeRunner();
        var seen = new List<string>();
        var exit = CliHost.Run(["mutation-diff", "--repo", _repo, "--target", "develop", "--source", "feature/x", "--out", _repo],
            new StringWriter(), new StringWriter(), m => { seen.Add(m); return 42; }, runner);
        exit.Should().Be(42);
        seen.Should().Equal("mutation-diff");
        runner.Calls.Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US3-AS29")]
    public void Weak_core_prints_fail_line_with_offenders_and_exits_1()
    {
        var r = Run(new FakeRunner(Killed(7, 3)));
        r.Exit.Should().Be(1);
        r.Out.Should().Contain("MutationDiff [core]: fail 70 (threshold 80)").And.Contain("App.Domain/A.cs:50 Boolean");
    }

    [Fact]
    [Trait("scenario", "US3-AS35")]
    public void Budget_exceeded_prints_timeout_message_and_records_timeout_verdict()
    {
        var r = Run(new FakeRunner(MutationRun.BudgetExceeded));
        r.Exit.Should().Be(2);
        r.Out.Should().Contain("30 minute");
        r.Verdict!.Value.GetProperty("results")[0].GetProperty("verdict").GetString().Should().Be("timeout");
    }

    [Fact]
    [Trait("scenario", "US3-AS36")]
    public void Invalid_gates_json_names_the_field_on_stderr_and_exits_3()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "quality"));
        File.WriteAllText(Path.Combine(_repo, "quality", "gates.json"), "{\"mutation\":{\"coreThreshold\":\"high\"}}");
        var r = Run(new FakeRunner());
        r.Exit.Should().Be(3);
        r.Err.Should().Contain("mutation.coreThreshold");
    }

    [Fact]
    [Trait("scenario", "US3-AS37")]
    public void Missing_report_exits_3_and_verdict_carries_the_error()
    {
        var r = Run(new FakeRunner(error: new MutationReportException("no mutation-report.json")));
        r.Exit.Should().Be(3);
        r.Verdict!.Value.GetProperty("error").GetString().Should().Contain("mutation-report.json");
    }

    [Fact]
    [Trait("scenario", "US3-AS38")]
    public void Missing_source_option_exits_3_and_names_it()
    {
        var se = new StringWriter();
        var exit = CliHost.Run(["mutation-diff", "--repo", _repo, "--target", "develop", "--out", _repo], new StringWriter(), se, null, new FakeRunner());
        exit.Should().Be(3);
        se.ToString().Should().Contain("--source");
    }

    [Fact]
    [Trait("scenario", "US3-AS39")]
    public void Verdict_json_is_camel_case_with_kebab_case_enums()
    {
        var r = Run(new FakeRunner(Killed(7, 3)), "--skip", "static", "--bypass-by", "bob");
        var v = r.Verdict!.Value;
        v.GetProperty("mode").GetString().Should().Be("mutation-diff");
        v.GetProperty("policy").GetString().Should().Be("blocking");
        v.GetProperty("sourceBranch").GetString().Should().Be("feature/x");
        v.GetProperty("targetBranch").GetString().Should().Be("develop");
        v.GetProperty("exitCode").GetInt32().Should().Be(1);
        v.GetProperty("skipped")[0].GetString().Should().Be("static");
        v.GetProperty("bypassedBy").GetString().Should().Be("bob");
        var res = v.GetProperty("results").EnumerateArray().First(e => e.GetProperty("layer").GetString() == "core");
        res.GetProperty("gate").GetString().Should().Be("mutation-diff");
        res.GetProperty("verdict").GetString().Should().Be("fail");
        res.GetProperty("offenders").GetArrayLength().Should().Be(3);
    }

    [Fact]
    [Trait("scenario", "US3-AS40")]
    public void Bypass_prints_line_and_does_not_invoke_the_runner()
    {
        var runner = new FakeRunner(Killed(7, 3));
        var r = Run(runner, "--skip", "mutation", "--bypass-by", "alice");
        r.Exit.Should().Be(0);
        r.Out.Should().Contain("bypassed by alice").And.Contain("skip (bypassed)");
        runner.Calls.Should().Be(0);
        r.Verdict!.Value.GetProperty("bypassedBy").GetString().Should().Be("alice");
    }

    [Fact]
    [Trait("scenario", "US3-AS41")]
    public void Bypass_by_alone_skips_all_groups()
    {
        var r = Run(new FakeRunner(Killed(7, 3)), "--bypass-by", "alice");
        r.Exit.Should().Be(0);
        r.Verdict!.Value.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()).Should().Equal("archdelta", "baselinegrowth", "branchage", "buildtrend", "complexity", "coupling", "coverage", "deadpublic", "duplication", "fanout", "featurereadme", "flows", "format", "ilgraph", "movedcoverage", "mutation", "prsize", "reports", "static", "testdiff", "toggles");
    }

    [Fact]
    [Trait("scenario", "US3-AS43")]
    public void Unknown_skip_group_exits_3_and_lists_allowed_groups()
    {
        var r = Run(new FakeRunner(), "--skip", "foo");
        r.Exit.Should().Be(3);
        r.Err.Should().Contain("static, coverage, mutation, format, duplication, baselinegrowth, coupling, prsize, archdelta, complexity, toggles, deadpublic, fanout, reports, featurereadme, flows, testdiff, ilgraph, branchage, buildtrend, movedcoverage or all");
    }

    [Fact]
    [Trait("scenario", "US3-AS45")]
    public void Skip_values_are_comma_separated_repeatable_and_blank_bypass_is_ignored()
    {
        var r = Run(new FakeRunner(Killed(9, 1)), "--skip", "static,coverage", "--skip", "static", "--bypass-by", "  ");
        r.Exit.Should().Be(0);
        var v = r.Verdict!.Value;
        v.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()).Should().Equal("coverage", "static");
        v.TryGetProperty("bypassedBy", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData(" Static , MUTATION ,")]
    [Trait("scenario", "US3-AS45")]
    public void Skip_groups_are_case_insensitive_trimmed_and_all_expands(string value)
    {
        var runner = new FakeRunner(Killed(7, 3));
        var r = Run(runner, "--skip", value);
        var skipped = r.Verdict!.Value.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()).ToList();
        if (value.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            skipped.Should().Equal("archdelta", "baselinegrowth", "branchage", "buildtrend", "complexity", "coupling", "coverage", "deadpublic", "duplication", "fanout", "featurereadme", "flows", "format", "ilgraph", "movedcoverage", "mutation", "prsize", "reports", "static", "testdiff", "toggles");
        }
        else
        {
            skipped.Should().Equal("mutation", "static");
        }

        runner.Calls.Should().Be(0);
        r.Exit.Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US3-AS41")]
    public void Explicit_skip_with_bypass_by_does_not_expand_to_all_and_still_runs_mutation()
    {
        var runner = new FakeRunner(Killed(7, 3));
        var r = Run(runner, "--skip", "static", "--bypass-by", "alice");
        runner.Calls.Should().Be(1);
        r.Exit.Should().Be(1);
        r.Verdict!.Value.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()).Should().Equal("static");
        r.Out.Should().Contain("bypassed by alice");
    }

    [Fact]
    [Trait("scenario", "US3-AS43")]
    public void Unknown_skip_group_names_the_token_and_writes_no_verdict()
    {
        var r = Run(new FakeRunner(), "--skip", "static,bogus");
        r.Err.Should().Contain("unknown --skip group 'bogus'");
        r.Verdict.Should().BeNull();
    }

    [Fact]
    [Trait("scenario", "US3-AS29")]
    public void Printer_error_goes_to_stderr_only_and_results_print_with_indented_offenders()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        ReportPrinterProbe.Print(new RunReport(RunMode.MutationDiff, Policy.Blocking, [], 3, Error: "boom"), so, se);
        se.ToString().Should().Be("boom" + Environment.NewLine);
        so.ToString().Should().BeEmpty();

        so = new StringWriter();
        se = new StringWriter();
        var results = new[]
        {
            new GateResult(GateId.MutationDiff, Verdict.Fail, ResultLayer.Core, 70, 80, ["a.cs:1 Boolean"]),
            new GateResult(GateId.MutationDiff, Verdict.Skip, null, Message: "msg"),
            new GateResult(GateId.MutationDiff, Verdict.Pass, null)
        };
        ReportPrinterProbe.Print(new RunReport(RunMode.MutationDiff, Policy.Blocking, results, 1), so, se);
        so.ToString().Should().Be(string.Join(Environment.NewLine,
            "MutationDiff [core]: fail 70 (threshold 80)", "  a.cs:1 Boolean", "MutationDiff: skip msg", "MutationDiff: pass") + Environment.NewLine);
        se.ToString().Should().BeEmpty();
    }
}
