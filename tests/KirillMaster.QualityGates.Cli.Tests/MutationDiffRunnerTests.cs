using FluentAssertions;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Application.UseCases;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class MutationDiffRunnerTests
{
    private const string Repo = "/repo";
    private const string CoreFile = "src/App.Domain/Pricing.cs";
    private const string AdapterFile = "src/App.Adapters/Repo.cs";

    private sealed class Fakes : IConfigStore, IGitClient, IProjectLocator, IPrFiles, IMutationRunner, IMutationVerdictStore
    {
        public ConfigLoadResult ConfigResult = ConfigLoadResult.Ok(GatesConfig.Default);
        public bool ConfigExists;
        public string Diff = DiffOf(CoreFile);
        public List<MutantResult> Mutants = [];
        public bool TimedOut;
        public Exception? Throw;
        public int RunCalls;
        public CancellationToken Token;
        public string? RunRepo, RunTarget, DiffRepo, DiffTarget, ExistsPath, LoadPath;
        public IReadOnlyDictionary<string, Layer>? Overrides;
        public List<(string Out, RunReport Report)> Written = [];

        public static string DiffOf(params string[] files) =>
            string.Concat(files.Select(f => $"diff --git a/{f} b/{f}\n--- a/{f}\n+++ b/{f}\n@@ -1,0 +1,1 @@\n+x\n"));

        public ConfigLoadResult Load(string path)
        {
            LoadPath = path;
            return ConfigResult;
        }

        public Task<string> DiffAsync(string repoPath, string targetBranch, CancellationToken cancellationToken = default)
        {
            DiffRepo = repoPath;
            DiffTarget = targetBranch;
            return Task.FromResult(Diff);
        }

        public Task<string> HeadAsync(string repoPath, CancellationToken cancellationToken = default) => Task.FromResult("abc");

        public ProjectRef? Locate(string repoRoot, string relativeFile, IReadOnlyDictionary<string, Layer> overrides) =>
            Capture(overrides) ?? (relativeFile.Contains(".Domain/", StringComparison.Ordinal) ? new ProjectRef("App.Domain", Layer.Core)
            : relativeFile.Contains(".Adapters/", StringComparison.Ordinal) ? new ProjectRef("App.Adapters", Layer.Adapter)
            : relativeFile.Contains(".Tests/", StringComparison.Ordinal) ? new ProjectRef("App.Tests", Layer.Test)
            : null);

        public IReadOnlyList<string> Expand(IEnumerable<string> patterns) => [];

        public bool Exists(string path)
        {
            ExistsPath = path;
            return ConfigExists;
        }

        private ProjectRef? Capture(IReadOnlyDictionary<string, Layer> o)
        {
            Overrides = o;
            return null;
        }

        public Task<MutationRun> RunAsync(string repoPath, string targetBranch, CancellationToken cancellationToken = default)
        {
            RunCalls++;
            RunRepo = repoPath;
            RunTarget = targetBranch;
            Token = cancellationToken;
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(TimedOut ? MutationRun.BudgetExceeded : MutationRun.Completed(Mutants));
        }

        public void Write(string outDirectory, RunReport report) => Written.Add((outDirectory, report));

        public MutationDiffRunner Runner() => new(this, this, this, this, this, this);
    }

    private static List<MutantResult> Score(string file, int killed, int survived)
    {
        var list = new List<MutantResult>();
        for (var i = 0; i < killed; i++)
        {
            list.Add(new MutantResult(file, i + 1, "Arithmetic", MutantStatus.Killed));
        }

        for (var i = 0; i < survived; i++)
        {
            list.Add(new MutantResult(file, 100 + i, "Boolean", MutantStatus.Survived));
        }

        return list;
    }

    private static MutationDiffRequest Req(string source = "feature/x", string target = "origin/develop", IReadOnlyCollection<QualityGroup>? skip = null, string? by = null) =>
        new(Repo, target, source, "/out", skip, by);

    [Fact]
    [Trait("scenario", "US3-AS29")]
    public async Task Weak_core_on_blocking_pair_fails_with_offenders()
    {
        var f = new Fakes { Mutants = Score(CoreFile, 7, 3) };
        var o = await f.Runner().RunAsync(Req());
        o.ExitCode.Should().Be(1);
        var core = o.Report.Results.Single(r => r.Layer == ResultLayer.Core);
        core.Verdict.Should().Be(Verdict.Fail);
        core.Actual.Should().Be(70);
        core.Threshold.Should().Be(80);
        core.Offenders.Should().Contain($"{CoreFile}:100 Boolean");
        f.Written.Should().ContainSingle().Which.Out.Should().Be("/out");
    }

    [Fact]
    [Trait("scenario", "US3-AS30")]
    public async Task Same_score_under_report_only_warns_with_exit_2()
    {
        var f = new Fakes { Mutants = Score(CoreFile, 7, 3) };
        var o = await f.Runner().RunAsync(Req(source: "spike/x"));
        o.ExitCode.Should().Be(2);
        o.Report.Policy.Should().Be(Policy.ReportOnly);
    }

    [Theory]
    [InlineData("release/2.17", "origin/master")]
    [InlineData("hotfix/x", "origin/master")]
    [InlineData("develop", "origin/release/2.17")]
    [Trait("scenario", "US3-AS31")]
    public async Task Skip_pairs_exit_zero_without_running_stryker(string source, string target)
    {
        var f = new Fakes { Mutants = Score(CoreFile, 0, 10) };
        var o = await f.Runner().RunAsync(Req(source, target));
        o.ExitCode.Should().Be(0);
        o.Report.Policy.Should().Be(Policy.Skip);
        o.Report.Results.Should().ContainSingle().Which.Verdict.Should().Be(Verdict.Skip);
        f.RunCalls.Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US3-AS32")]
    public async Task Readme_only_diff_is_not_applicable_without_running_stryker()
    {
        var f = new Fakes { Diff = Fakes.DiffOf("README.md") };
        var o = await f.Runner().RunAsync(Req());
        o.ExitCode.Should().Be(0);
        o.Report.Results.Should().ContainSingle().Which.Verdict.Should().Be(Verdict.NotApplicable);
        f.RunCalls.Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US3-AS33")]
    public async Task Test_csproj_and_json_only_diff_does_not_run_stryker()
    {
        var f = new Fakes { Diff = Fakes.DiffOf("src/App.Tests/FooTests.cs", "src/App.Domain/App.Domain.csproj", "quality/gates.json") };
        var o = await f.Runner().RunAsync(Req());
        o.ExitCode.Should().Be(0);
        o.Report.Results.Single().Verdict.Should().Be(Verdict.NotApplicable);
        f.RunCalls.Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US3-AS34")]
    public async Task Core_pass_with_adapter_warn_exits_2()
    {
        var f = new Fakes { Diff = Fakes.DiffOf(CoreFile, AdapterFile), Mutants = [.. Score(CoreFile, 9, 1), .. Score(AdapterFile, 4, 6)] };
        var o = await f.Runner().RunAsync(Req());
        o.ExitCode.Should().Be(2);
        o.Report.Results.Single(r => r.Layer == ResultLayer.Core).Verdict.Should().Be(Verdict.Pass);
        o.Report.Results.Single(r => r.Layer == ResultLayer.Adapter).Verdict.Should().Be(Verdict.Warn);
    }

    [Fact]
    [Trait("scenario", "US3-AS35")]
    public async Task Budget_exceeded_is_timeout_with_exit_2_and_budget_in_message()
    {
        var f = new Fakes { TimedOut = true };
        var o = await f.Runner().RunAsync(Req());
        o.ExitCode.Should().Be(2);
        var r = o.Report.Results.Single();
        r.Verdict.Should().Be(Verdict.Timeout);
        r.Message.Should().Contain("30 minute");
        f.Token.CanBeCanceled.Should().BeTrue();
    }

    [Fact]
    [Trait("scenario", "US3-AS36")]
    public async Task Invalid_config_is_input_error_naming_the_field()
    {
        var f = new Fakes { ConfigExists = true, ConfigResult = ConfigLoadResult.Fail("mutation.coreThreshold", "expected a number") };
        var o = await f.Runner().RunAsync(Req());
        o.ExitCode.Should().Be(3);
        o.Report.Error.Should().Contain("mutation.coreThreshold");
        f.RunCalls.Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US3-AS37")]
    public async Task Missing_report_is_input_error_recorded_in_verdict()
    {
        var f = new Fakes { Throw = new MutationReportException("stryker did not write mutation-report.json") };
        var o = await f.Runner().RunAsync(Req());
        o.ExitCode.Should().Be(3);
        o.Report.Error.Should().Contain("mutation-report.json");
        f.Written.Single().Report.Error.Should().NotBeNull();
    }

    [Fact]
    [Trait("scenario", "US3-AS40")]
    public async Task Bypassed_mutation_is_skip_bypassed_and_never_fails()
    {
        var f = new Fakes { Mutants = Score(CoreFile, 7, 3) };
        var o = await f.Runner().RunAsync(Req(skip: [QualityGroup.Mutation], by: "alice"));
        o.ExitCode.Should().Be(0);
        var r = o.Report.Results.Single();
        r.Verdict.Should().Be(Verdict.Skip);
        r.Message.Should().Be("skip (bypassed)");
        o.Report.BypassedBy.Should().Be("alice");
        o.Report.SkippedGroups.Should().Equal("mutation");
        f.RunCalls.Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US3-AS41")]
    public async Task All_groups_skipped_are_recorded_sorted()
    {
        var f = new Fakes();
        var o = await f.Runner().RunAsync(Req(skip: [QualityGroup.Static, QualityGroup.Coverage, QualityGroup.Mutation], by: "alice"));
        o.ExitCode.Should().Be(0);
        o.Report.SkippedGroups.Should().Equal("coverage", "mutation", "static");
    }

    [Fact]
    [Trait("scenario", "US3-AS42")]
    public async Task Skipping_other_groups_still_evaluates_mutation()
    {
        var f = new Fakes { Mutants = Score(CoreFile, 7, 3) };
        var o = await f.Runner().RunAsync(Req(skip: [QualityGroup.Static, QualityGroup.Coverage]));
        o.ExitCode.Should().Be(1);
        o.Report.SkippedGroups.Should().Equal("coverage", "static");
        f.RunCalls.Should().Be(1);
    }

    [Fact]
    [Trait("scenario", "US3-AS44")]
    public async Task Bypass_on_skip_pair_stays_skip_and_records_the_bypass()
    {
        var f = new Fakes();
        var o = await f.Runner().RunAsync(Req("release/2.17", "origin/master", [QualityGroup.Mutation], "alice"));
        o.ExitCode.Should().Be(0);
        o.Report.Policy.Should().Be(Policy.Skip);
        o.Report.BypassedBy.Should().Be("alice");
    }

    [Fact]
    [Trait("scenario", "US3-AS29")]
    public async Task Run_receives_repo_and_target_and_verdict_report_matches_outcome()
    {
        var f = new Fakes { Mutants = Score(CoreFile, 7, 3) };
        var o = await f.Runner().RunAsync(Req());
        f.RunRepo.Should().Be(Repo);
        f.RunTarget.Should().Be("origin/develop");
        f.DiffRepo.Should().Be(Repo);
        f.DiffTarget.Should().Be("origin/develop");
        var w = f.Written.Single().Report;
        w.ExitCode.Should().Be(1);
        w.Mode.Should().Be(RunMode.MutationDiff);
        w.Policy.Should().Be(Policy.Blocking);
        w.SourceBranch.Should().Be("feature/x");
        w.TargetBranch.Should().Be("origin/develop");
        w.BypassedBy.Should().BeNull();
        w.SkippedGroups.Should().BeEmpty();
        w.Error.Should().BeNull();
        w.Should().BeSameAs(o.Report);
    }

    [Fact]
    [Trait("scenario", "US3-AS34")]
    public async Task Adapter_only_diff_runs_stryker_and_evaluates_adapter()
    {
        var f = new Fakes { Diff = Fakes.DiffOf(AdapterFile), Mutants = Score(AdapterFile, 4, 6) };
        var o = await f.Runner().RunAsync(Req());
        f.RunCalls.Should().Be(1);
        o.ExitCode.Should().Be(2);
        o.Report.Results.Should().Contain(r => r.Layer == ResultLayer.Adapter && r.Verdict == Verdict.Warn);
    }

    [Fact]
    [Trait("scenario", "US3-AS33")]
    public async Task Non_cs_file_under_core_project_is_not_production_code()
    {
        var f = new Fakes { Diff = Fakes.DiffOf("src/App.Domain/data.json", "src/App.Adapters/readme.md") };
        var o = await f.Runner().RunAsync(Req());
        f.RunCalls.Should().Be(0);
        o.Report.Results.Single().Message.Should().Be("no production code changed");
        o.Report.Results.Single().Layer.Should().Be(ResultLayer.All);
        f.Written.Should().ContainSingle().Which.Out.Should().Be("/out");
    }

    [Fact]
    [Trait("scenario", "US3-AS33")]
    public async Task Uppercase_cs_extension_counts_as_production_code()
    {
        var f = new Fakes { Diff = Fakes.DiffOf("src/App.Domain/Upper.CS"), Mutants = Score("src/App.Domain/Upper.CS", 10, 0) };
        await f.Runner().RunAsync(Req());
        f.RunCalls.Should().Be(1);
    }

    [Fact]
    [Trait("scenario", "US3-AS36")]
    public async Task Invalid_config_reads_quality_gates_json_and_records_failure_verdict()
    {
        var f = new Fakes { ConfigExists = true, ConfigResult = ConfigLoadResult.Fail("mutation.coreThreshold", "expected a number") };
        var o = await f.Runner().RunAsync(Req(skip: [QualityGroup.Static], by: "alice"));
        f.ExistsPath.Should().EndWith("gates.json").And.Contain("quality");
        f.LoadPath.Should().Be(f.ExistsPath);
        o.Report.Error.Should().Be("invalid gates.json, field mutation.coreThreshold: expected a number");
        o.Report.Results.Should().BeEmpty();
        o.Report.SourceBranch.Should().Be("feature/x");
        o.Report.TargetBranch.Should().Be("origin/develop");
        o.Report.BypassedBy.Should().Be("alice");
        o.Report.SkippedGroups.Should().Equal("static");
        o.Report.Policy.Should().Be(Policy.Blocking);
        f.Written.Should().ContainSingle().Which.Out.Should().Be("/out");
    }

    [Fact]
    [Trait("scenario", "US3-AS36")]
    public async Task Absent_config_file_is_never_loaded_and_defaults_apply()
    {
        var f = new Fakes { ConfigExists = false, ConfigResult = ConfigLoadResult.Fail("x", "boom"), Mutants = Score(CoreFile, 7, 3) };
        var o = await f.Runner().RunAsync(Req());
        f.LoadPath.Should().BeNull();
        o.ExitCode.Should().Be(1);
        o.Report.Error.Should().BeNull();
    }

    [Fact]
    [Trait("scenario", "US3-AS36")]
    public async Task Loaded_config_threshold_and_overrides_are_used()
    {
        var cfg = GatesConfig.Default with { Mutation = new MutationSettings(CoreThreshold: 60), LayerOverrides = new Dictionary<string, Layer> { ["x"] = Layer.Core } };
        var f = new Fakes { ConfigExists = true, ConfigResult = ConfigLoadResult.Ok(cfg), Mutants = Score(CoreFile, 7, 3) };
        var o = await f.Runner().RunAsync(Req());
        o.ExitCode.Should().Be(0);
        o.Report.Results.Single(r => r.Layer == ResultLayer.Core).Threshold.Should().Be(60);
        f.Overrides.Should().BeSameAs(cfg.LayerOverrides);
    }

    [Fact]
    [Trait("scenario", "US3-AS35")]
    public async Task Timeout_message_uses_configured_budget_and_token_not_fired_early()
    {
        var cfg = GatesConfig.Default with { Mutation = new MutationSettings(DiffBudgetMinutes: 7) };
        var f = new Fakes { ConfigExists = true, ConfigResult = ConfigLoadResult.Ok(cfg), TimedOut = true };
        var o = await f.Runner().RunAsync(Req());
        f.Token.IsCancellationRequested.Should().BeFalse();
        o.Report.Results.Single().Message.Should().Contain("7 minute");
    }

    [Fact]
    [Trait("scenario", "US3-AS35")]
    public async Task Timeout_report_is_recorded_with_exit_2_in_verdict()
    {
        var f = new Fakes { TimedOut = true };
        var o = await f.Runner().RunAsync(Req(skip: [QualityGroup.Coverage]));
        f.Written.Single().Report.ExitCode.Should().Be(2);
        f.Written.Single().Report.Results.Single().Verdict.Should().Be(Verdict.Timeout);
        o.Report.SkippedGroups.Should().Equal("coverage");
    }

    [Fact]
    [Trait("scenario", "US3-AS40")]
    public async Task Mutation_bypass_wins_over_not_applicable_and_is_written()
    {
        var f = new Fakes { Diff = Fakes.DiffOf("README.md") };
        var o = await f.Runner().RunAsync(Req(skip: [QualityGroup.Mutation], by: "alice"));
        o.Report.Results.Single().Verdict.Should().Be(Verdict.Skip);
        o.Report.Results.Single().Layer.Should().Be(ResultLayer.All);
        f.Written.Should().ContainSingle().Which.Report.BypassedBy.Should().Be("alice");
        f.DiffRepo.Should().BeNull();
    }

    [Fact]
    [Trait("scenario", "US3-AS31")]
    public async Task Skip_pair_does_not_consult_git_and_writes_verdict()
    {
        var f = new Fakes();
        await f.Runner().RunAsync(Req("release/2.17", "origin/master"));
        f.DiffRepo.Should().BeNull();
        f.Written.Should().ContainSingle().Which.Out.Should().Be("/out");
    }
}
