using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Json;
using KirillMaster.QualityGates.Cli.Adapters.Stryker;
using KirillMaster.QualityGates.Cli.Application;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public sealed class MutationDiffGateTests
{
    private const string CoreFile = "src/Sample.Domain/Calc.cs";
    private const string AdapterFile = "src/Sample.Adapters/Repo.cs";

    private static string Report(string name) => File.ReadAllText(Fx.Path("mutation", name));

    private static Layer LayerOf(string path, IReadOnlyDictionary<string, Layer>? overrides = null)
    {
        var project = path.Replace('\\', '/').Split('/').FirstOrDefault(s => s.StartsWith("Sample.", StringComparison.Ordinal)) ?? "Unknown";
        return LayerClassifier.Classify(project, path, overrides).Layer;
    }

    private static List<MutantResult> Mutants(string file, int detected, int survived = 0, int noCoverage = 0)
    {
        var list = new List<MutantResult>();
        var line = 1;
        list.AddRange(Enumerable.Range(0, detected).Select(_ => new MutantResult(file, line++, "Arithmetic", MutantStatus.Killed)));
        list.AddRange(Enumerable.Range(0, survived).Select(_ => new MutantResult(file, line++, "Boolean", MutantStatus.Survived)));
        list.AddRange(Enumerable.Range(0, noCoverage).Select(_ => new MutantResult(file, line++, "Linq", MutantStatus.NoCoverage)));
        return list;
    }

    private static IReadOnlyList<GateResult> Evaluate(IReadOnlyList<MutantResult> mutants, Policy policy, GatesConfig? config = null, bool timedOut = false) =>
        MutationDiffGate.Evaluate(mutants, p => LayerOf(p), config ?? GatesConfig.Default, policy, timedOut);

    private static GateResult Core(IEnumerable<GateResult> results) => results.Single(r => r.Layer == ResultLayer.Core);

    private static GateResult Adapter(IEnumerable<GateResult> results) => results.Single(r => r.Layer == ResultLayer.Adapter);

    private static int ExitCode(IReadOnlyList<GateResult> results, Policy policy) =>
        ExitCodeMapper.Map(new RunReport(RunMode.MutationDiff, policy, results, 0));

    [Fact]
    [Trait("scenario", "US3-AS7")]
    public void Score_follows_stryker_definition_and_excludes_non_scored_statuses()
    {
        var mutants = MutationReportParser.Parse(Report("statuses-mixed.json"), "C:\\repo");

        mutants.Should().HaveCount(16);
        mutants.Count(m => m.IsDetected).Should().Be(8);
        mutants.Count(m => m.IsUndetected).Should().Be(2);
        mutants.Where(m => m.Status is MutantStatus.Ignored or MutantStatus.CompileError or MutantStatus.RuntimeError)
            .Should().OnlyContain(m => !m.IsDetected && !m.IsUndetected);

        var core = Core(Evaluate(mutants, Policy.Blocking));
        core.Actual.Should().Be(80);
        core.Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    [Trait("scenario", "US3-AS7")]
    public void Parser_reads_real_schema_with_relative_forward_slash_paths_and_line_numbers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mutants = MutationReportParser.Parse(Report("statuses-mixed.json"), "C:\\repo");

        mutants.Should().OnlyContain(m => m.File == "src/Sample.Domain/Calc.cs");
        mutants[0].Line.Should().Be(10);
        mutants[0].Mutator.Should().Be("Arithmetic");
        mutants[8].Status.Should().Be(MutantStatus.Survived);
    }

    [Fact]
    [Trait("scenario", "US3-AS8")]
    public void NoCoverage_counts_as_survived()
    {
        var core = Core(Evaluate(Mutants(CoreFile, detected: 7, noCoverage: 3), Policy.Blocking));

        core.Actual.Should().Be(70);
        core.Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    [Trait("scenario", "US3-AS9")]
    public void Files_are_aggregated_per_layer_and_test_files_are_ignored()
    {
        var mutants = Mutants("src/Sample.Domain/A.cs", 1)
            .Concat(Mutants("src/Sample.Application/B.cs", 0, survived: 1))
            .Concat(Mutants("src/Sample.Adapters/C.cs", 1, survived: 1))
            .Concat(Mutants("tests/Sample.Tests/D.cs", 0, survived: 5))
            .ToList();

        var results = Evaluate(mutants, Policy.Blocking);

        Core(results).Actual.Should().Be(50);
        Adapter(results).Actual.Should().Be(50);
    }

    [Fact]
    [Trait("scenario", "US3-AS9")]
    public void Layer_overrides_are_honoured()
    {
        var overrides = new Dictionary<string, Layer> { ["Sample.Adapters"] = Layer.Core, ["Sample.Domain"] = Layer.Excluded };
        var mutants = Mutants("src/Sample.Domain/A.cs", 0, survived: 4)
            .Concat(Mutants("src/Sample.Adapters/C.cs", 3, survived: 1))
            .ToList();

        var results = MutationDiffGate.Evaluate(mutants, p => LayerOf(p, overrides), GatesConfig.Default, Policy.Blocking);

        Core(results).Actual.Should().Be(75);
        Adapter(results).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    [Trait("scenario", "US3-AS10")]
    public void Core_below_threshold_under_blocking_fails_with_offenders()
    {
        var core = Core(Evaluate(Mutants(CoreFile, detected: 7, survived: 1, noCoverage: 2), Policy.Blocking));

        core.Verdict.Should().Be(Verdict.Fail);
        core.Actual.Should().Be(70);
        core.Threshold.Should().Be(80);
        core.Offenders.Should().BeEquivalentTo(
            [$"{CoreFile}:8 Boolean", $"{CoreFile}:9 Linq", $"{CoreFile}:10 Linq"],
            o => o.WithStrictOrdering());
    }

    [Theory]
    [Trait("scenario", "US3-AS11")]
    [InlineData(10, 0, Verdict.Pass)]
    [InlineData(8, 2, Verdict.Pass)]
    [InlineData(7999, 2001, Verdict.Fail)]
    [InlineData(0, 10, Verdict.Fail)]
    public void Core_threshold_boundary(int killed, int survived, Verdict expected)
    {
        var core = Core(Evaluate(Mutants(CoreFile, killed, survived), Policy.Blocking));

        core.Verdict.Should().Be(expected);
    }

    [Fact]
    [Trait("scenario", "US3-AS12")]
    public void Core_below_threshold_under_report_only_warns_with_exit_code_2()
    {
        var results = Evaluate(Mutants(CoreFile, detected: 7, survived: 3), Policy.ReportOnly);

        Core(results).Verdict.Should().Be(Verdict.Warn);
        ExitCode(results, Policy.ReportOnly).Should().Be(2);
    }

    [Fact]
    [Trait("scenario", "US3-AS13")]
    public void Skip_policy_produces_skip_without_evaluating_scores()
    {
        var results = Evaluate(Mutants(CoreFile, detected: 1, survived: 9), Policy.Skip);

        results.Should().ContainSingle().Which.Verdict.Should().Be(Verdict.Skip);
        results[0].Actual.Should().BeNull();
        ExitCode(results, Policy.Skip).Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US3-AS14")]
    public void Adapter_below_threshold_warns_and_never_fails()
    {
        var mutants = Mutants(CoreFile, detected: 19, survived: 1).Concat(Mutants(AdapterFile, detected: 3, survived: 7)).ToList();

        var results = Evaluate(mutants, Policy.Blocking);

        Adapter(results).Verdict.Should().Be(Verdict.Warn);
        Core(results).Verdict.Should().Be(Verdict.Pass);
        ExitCode(results, Policy.Blocking).Should().Be(2);
    }

    [Fact]
    [Trait("scenario", "US3-AS15")]
    public void Adapter_at_threshold_passes()
    {
        var results = Evaluate(Mutants(AdapterFile, detected: 8, survived: 2), Policy.Blocking);

        Adapter(results).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    [Trait("scenario", "US3-AS16")]
    public void Layer_without_mutants_is_not_applicable()
    {
        var core = Core(Evaluate(Mutants(AdapterFile, detected: 1), Policy.Blocking));

        core.Verdict.Should().Be(Verdict.NotApplicable);
        core.Threshold.Should().BeNull();
    }

    [Fact]
    [Trait("scenario", "US3-AS17")]
    public void Empty_report_passes_as_not_applicable()
    {
        var mutants = MutationReportParser.Parse(Report("empty.json"), "C:\\repo");

        var results = Evaluate(mutants, Policy.Blocking);

        results.Should().OnlyContain(r => r.Verdict == Verdict.NotApplicable);
        ExitCode(results, Policy.Blocking).Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US3-AS18")]
    public void Core_threshold_comes_from_config()
    {
        var config = GatesConfig.Default with { Mutation = GatesConfig.Default.Mutation with { CoreThreshold = 90 } };

        var core = Core(Evaluate(Mutants(CoreFile, detected: 17, survived: 3), Policy.Blocking, config));

        core.Verdict.Should().Be(Verdict.Fail);
        core.Threshold.Should().Be(90);
    }

    [Fact]
    [Trait("scenario", "US3-AS19")]
    public void Timeout_is_timeout_verdict_with_budget_message_and_exit_code_2()
    {
        var results = Evaluate([], Policy.Blocking, timedOut: true);

        var result = results.Should().ContainSingle().Subject;
        result.Verdict.Should().Be(Verdict.Timeout);
        result.Message.Should().Contain("30 minute").And.Contain("UNSTABLE");
        ExitCode(results, Policy.Blocking).Should().Be(2);
    }

    [Fact]
    [Trait("scenario", "US3-AS20")]
    public void Timeout_never_becomes_fail_and_partial_score_is_not_evaluated()
    {
        var results = Evaluate(Mutants(CoreFile, detected: 1, survived: 9), Policy.Blocking, timedOut: true);

        results.Should().ContainSingle().Which.Verdict.Should().Be(Verdict.Timeout);
        results[0].Actual.Should().BeNull();
        results.Should().NotContain(r => r.Verdict == Verdict.Fail);
    }

    [Fact]
    [Trait("scenario", "US3-AS21")]
    public async Task Stryker_command_is_diff_only_with_json_reporter_and_unit_test_projects()
    {
        using var repo = TempRepo.Create();
        repo.Touch("Sample.sln");
        repo.Touch("tests/Sample.Tests/Sample.Tests.csproj");
        repo.Touch("tests/Sample.IntegrationTests/Sample.IntegrationTests.csproj");
        repo.Touch("src/Sample.Domain/Sample.Domain.csproj");
        ProcessCommand? seen = null;
        var runner = new StrykerRunner(new FakeProcess((c, _) =>
        {
            seen = c;
            repo.WriteReport("StrykerOutput/20260101/reports/mutation-report.json", Report("statuses-mixed.json"));
            return Task.FromResult(new ProcessResult(0));
        }));

        await runner.RunAsync(repo.Path, "origin/develop");

        seen.Should().NotBeNull();
        seen!.FileName.Should().Be("dotnet");
        seen.WorkingDirectory.Should().Be(repo.Path);
        seen.Arguments.Should().StartWith(["stryker", "--since:origin/develop", "--reporter", "json"]);
        seen.Arguments.Should().Contain("--test-project").And.Contain("tests/Sample.Tests/Sample.Tests.csproj");
        seen.Arguments.Should().NotContain(a => a.Contains("IntegrationTests", StringComparison.Ordinal));
        seen.Arguments.Should().NotContain("--with-baseline");
        seen.Arguments.Should().ContainInOrder("--solution", "Sample.sln");
    }

    [Fact]
    [Trait("scenario", "US3-AS22")]
    public async Task Newest_new_report_is_parsed_into_per_file_mutants()
    {
        using var repo = TempRepo.Create();
        repo.WriteReport("StrykerOutput/20250101/reports/mutation-report.json", "{\"files\":{\"stale.cs\":{\"mutants\":[]}}}");
        var runner = new StrykerRunner(new FakeProcess((_, _) =>
        {
            repo.WriteReport("StrykerOutput/20260101/reports/mutation-report.json", Report("statuses-mixed.json").Replace("C:\\\\repo", repo.Path.Replace("\\", "\\\\")));
            return Task.FromResult(new ProcessResult(0));
        }));

        var run = await runner.RunAsync(repo.Path, "develop");

        run.TimedOut.Should().BeFalse();
        run.Mutants.Should().HaveCount(16);
        run.Mutants[0].File.Should().Be("src/Sample.Domain/Calc.cs");
        run.Mutants[0].Line.Should().Be(10);
        run.Mutants.Count(m => m.Status == MutantStatus.Timeout).Should().Be(2);
    }

    [Fact]
    [Trait("scenario", "US3-AS23")]
    public async Task Missing_report_is_an_input_error_naming_the_expected_path()
    {
        using var repo = TempRepo.Create();
        var runner = new StrykerRunner(new FakeProcess((_, _) => Task.FromResult(new ProcessResult(1))));

        var act = () => runner.RunAsync(repo.Path, "develop");

        var ex = await act.Should().ThrowAsync<MutationReportException>();
        ex.Which.Message.Should().Contain("mutation-report.json").And.Contain("StrykerOutput").And.Contain("exit code 1");
    }

    [Fact]
    [Trait("scenario", "US3-AS23")]
    public async Task Malformed_report_is_an_input_error()
    {
        using var repo = TempRepo.Create();
        var runner = new StrykerRunner(new FakeProcess((_, _) =>
        {
            repo.WriteReport("StrykerOutput/20260101/reports/mutation-report.json", Report("malformed.json"));
            return Task.FromResult(new ProcessResult(0));
        }));

        var act = () => runner.RunAsync(repo.Path, "develop");

        (await act.Should().ThrowAsync<MutationReportException>()).Which.Message.Should().Contain("invalid json");
    }

    [Theory]
    [Trait("scenario", "US3-AS23")]
    [InlineData("{}")]
    [InlineData("{\"files\":{\"a.cs\":{}}}")]
    [InlineData("{\"files\":{\"a.cs\":{\"mutants\":[{\"status\":\"Killed\"}]}}}")]
    [InlineData("{\"files\":{\"a.cs\":{\"mutants\":[{\"location\":{\"start\":{\"line\":1}}}]}}}")]
    public void Report_missing_required_fields_is_an_input_error(string json)
    {
        var act = () => MutationReportParser.Parse(json, "C:\\repo");

        act.Should().Throw<MutationReportException>();
    }

    [Fact]
    [Trait("scenario", "US3-AS24")]
    public async Task Budget_cancellation_reports_timeout_without_scoring_a_partial_report()
    {
        using var repo = TempRepo.Create();
        var cancelled = false;
        var runner = new StrykerRunner(new FakeProcess(async (_, ct) =>
        {
            repo.WriteReport("StrykerOutput/20260101/reports/mutation-report.json", Report("statuses-mixed.json"));
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return new ProcessResult(0);
        }));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var run = await runner.RunAsync(repo.Path, "develop", cts.Token);

        cancelled.Should().BeTrue();
        run.TimedOut.Should().BeTrue();
        run.Mutants.Should().BeEmpty();
        MutationDiffGate.Evaluate(run.Mutants, p => LayerOf(p), GatesConfig.Default, Policy.Blocking, run.TimedOut)
            .Should().ContainSingle().Which.Verdict.Should().Be(Verdict.Timeout);
    }

    [Fact]
    [Trait("scenario", "US3-AS24")]
    public async Task Cancellation_terminates_the_real_process_tree()
    {
        var command = OperatingSystem.IsWindows()
            ? new ProcessCommand("powershell", ["-NoProfile", "-Command", "Start-Sleep -Seconds 120"], Path.GetTempPath())
            : new ProcessCommand("sleep", ["120"], Path.GetTempPath());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var act = () => new SystemProcessRunner().RunAsync(command, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    [Fact]
    [Trait("scenario", "US3-AS25")]
    public void Budget_comes_from_config_with_a_30_minute_default()
    {
        using var dir = TempRepo.Create();
        dir.WriteReport("gates.json", "{\"mutation\":{\"diffBudgetMinutes\":5}}");
        var explicitConfig = new JsonConfigStore().Load(Path.Combine(dir.Path, "gates.json")).Config!;

        MutationBudget.Diff(explicitConfig).Should().Be(TimeSpan.FromMinutes(5));
        MutationBudget.Diff(GatesConfig.Default).Should().Be(TimeSpan.FromMinutes(30));

        using var source = MutationBudget.CreateDiffSource(explicitConfig);
        source.IsCancellationRequested.Should().BeFalse();
    }

    internal sealed class FakeProcess(Func<ProcessCommand, CancellationToken, Task<ProcessResult>> run) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken) => run(command, cancellationToken);
    }

    internal sealed class TempRepo : IDisposable
    {
        private TempRepo(string path) => Path = path;

        public string Path { get; }

        public static TempRepo Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qg-mut-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(path);
            return new TempRepo(path);
        }

        public void Touch(string relative) => WriteReport(relative, "");

        public void WriteReport(string relative, string content)
        {
            var parts = relative.Split('\n');
            var (rel, body) = parts.Length == 2 ? (parts[0], parts[1]) : (relative, content);
            var full = System.IO.Path.Combine(Path, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
