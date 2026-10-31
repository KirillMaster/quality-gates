using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Stryker;
using KirillMaster.QualityGates.Cli.Application;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public sealed class MutationHardeningTests
{
    private const string CoreFile = "src/Sample.Domain/Calc.cs";
    private const string ReportRelative = "StrykerOutput/20260101/reports/mutation-report.json";

    private static string Report(string name) => File.ReadAllText(Fx.Path("mutation", name));

    private static string Mutant(string status, string mutatorFragment, int line) =>
        "{\"id\":\"" + line + "\",\"status\":\"" + status + "\"," + mutatorFragment
        + "\"location\":{\"start\":{\"line\":" + line + ",\"column\":1},\"end\":{\"line\":" + line + ",\"column\":2}}}";

    private static string FileJson(params string[] mutants) =>
        "{\"files\":{\"" + CoreFile + "\":{\"mutants\":[" + string.Join(",", mutants) + "]}}}";

    private static Layer Core(string _) => Layer.Core;

    private static void WriteStandardReport(MutationDiffGateTests.TempRepo repo) =>
        repo.WriteReport(ReportRelative, Report("statuses-mixed.json"));

    private static async Task<ProcessCommand> CaptureCommand(
        MutationDiffGateTests.TempRepo repo,
        Action<MutationDiffGateTests.TempRepo> writes)
    {
        ProcessCommand? seen = null;
        var runner = new StrykerRunner(new MutationDiffGateTests.FakeProcess((c, _) =>
        {
            seen = c;
            writes(repo);
            return Task.FromResult(new ProcessResult(0));
        }));
        await runner.RunAsync(repo.Path, "origin/develop");
        return seen!;
    }

    [Fact]
    [Trait("scenario", "US3-AS7")]
    public void Every_stryker_status_maps_to_its_domain_status_and_unknown_maps_to_other()
    {
        var statuses = new[] { "Killed", "Timeout", "Survived", "NoCoverage", "Ignored", "CompileError", "RuntimeError", "Pending" };
        var json = FileJson(statuses.Select((s, i) => Mutant(s, "\"mutatorName\":\"Boolean\",", i + 1)).ToArray());

        var mutants = MutationReportParser.Parse(json, "C:/repo");

        mutants.Select(m => m.Status).Should().Equal(
            MutantStatus.Killed, MutantStatus.Timeout, MutantStatus.Survived, MutantStatus.NoCoverage,
            MutantStatus.Ignored, MutantStatus.CompileError, MutantStatus.RuntimeError, MutantStatus.Other);
        mutants.Select(m => m.Mutator).Distinct().Should().Equal("Boolean");
    }

    [Fact]
    [Trait("scenario", "US3-AS7")]
    public void Missing_or_non_string_mutator_name_is_reported_as_unknown_mutator()
    {
        var json = FileJson(
            Mutant("Survived", "", 1),
            Mutant("Survived", "\"mutatorName\":42,", 2),
            Mutant("Survived", "\"mutatorName\":\"Linq\",", 3));

        var mutants = MutationReportParser.Parse(json, "C:/repo");

        mutants.Select(m => m.Mutator).Should().Equal("unknown mutator", "unknown mutator", "Linq");
        mutants.Select(m => m.Line).Should().Equal(1, 2, 3);
    }

    [Fact]
    [Trait("scenario", "US3-AS22")]
    public async Task Newest_report_wins_by_last_write_time_not_by_name_order()
    {
        using var repo = MutationDiffGateTests.TempRepo.Create();
        var older = Path.Combine(repo.Path, "StrykerOutput/20260101/reports/mutation-report.json");
        var newer = Path.Combine(repo.Path, "StrykerOutput/20260102/reports/mutation-report.json");

        var runner = new StrykerRunner(new MutationDiffGateTests.FakeProcess((_, _) =>
        {
            repo.WriteReport("StrykerOutput/20260101/reports/mutation-report.json", Report("empty.json"));
            repo.WriteReport("StrykerOutput/20260102/reports/mutation-report.json", Report("statuses-mixed.json"));
            File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            return Task.FromResult(new ProcessResult(0));
        }));

        var run = await runner.RunAsync(repo.Path, "origin/develop");

        run.Mutants.Should().HaveCount(MutationReportParser.Parse(Report("statuses-mixed.json"), repo.Path).Count);
        run.Mutants.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("scenario", "US3-AS23")]
    public async Task Report_outside_stryker_output_is_never_picked_up()
    {
        using var repo = MutationDiffGateTests.TempRepo.Create();
        var runner = new StrykerRunner(new MutationDiffGateTests.FakeProcess((_, _) =>
        {
            repo.WriteReport("elsewhere/mutation-report.json", Report("statuses-mixed.json"));
            return Task.FromResult(new ProcessResult(0));
        }));

        await FluentActions.Awaiting(() => runner.RunAsync(repo.Path, "origin/develop"))
            .Should().ThrowAsync<MutationReportException>();
    }

    [Fact]
    [Trait("scenario", "US3-AS23")]
    public async Task Missing_report_error_names_exit_code_expected_path_and_output_tail()
    {
        using var repo = MutationDiffGateTests.TempRepo.Create();
        var runner = new StrykerRunner(new MutationDiffGateTests.FakeProcess((_, _) =>
            Task.FromResult(new ProcessResult(3, "boom"))));

        var thrown = await FluentActions.Awaiting(() => runner.RunAsync(repo.Path, "origin/develop"))
            .Should().ThrowAsync<MutationReportException>();

        var expected = "stryker (exit code 3) did not write a new mutation-report.json; expected "
            + Path.Combine(Path.GetFullPath(repo.Path), "StrykerOutput", "<timestamp>", "reports", "mutation-report.json")
            + ". Output tail: boom";
        thrown.Which.Message.Should().Be(expected);
    }

    [Fact]
    [Trait("scenario", "US3-AS21")]
    public async Task Projects_under_skipped_directories_are_never_listed_as_test_projects()
    {
        using var repo = MutationDiffGateTests.TempRepo.Create();
        foreach (var dir in new[] { "bin", "obj", "node_modules", "StrykerOutput", ".git" })
        {
            repo.Touch($"{dir}/Ghost.Tests/Ghost.Tests.csproj");
        }

        var command = await CaptureCommand(repo, WriteStandardReport);

        command.Arguments.Should().NotContain("--test-project");
    }

    [Fact]
    [Trait("scenario", "US3-AS21")]
    public async Task Solution_is_chosen_by_ordinal_name()
    {
        using var repo = MutationDiffGateTests.TempRepo.Create();
        repo.Touch("B.sln");
        repo.Touch("A.sln");
        repo.Touch("-readme.txt");

        var command = await CaptureCommand(repo, WriteStandardReport);

        command.Arguments.Should().ContainInOrder("--solution", "A.sln");
        command.Arguments.Should().NotContain("B.sln");
    }

    [Fact]
    [Trait("scenario", "US3-AS21")]
    public async Task No_solution_file_omits_the_solution_argument()
    {
        using var repo = MutationDiffGateTests.TempRepo.Create();

        var command = await CaptureCommand(repo, WriteStandardReport);

        command.Arguments.Should().NotContain("--solution");
    }

    [Fact]
    [Trait("scenario", "US3-AS21")]
    public async Task Only_unit_test_projects_are_passed_in_ordinal_order()
    {
        using var repo = MutationDiffGateTests.TempRepo.Create();
        repo.Touch("tests/Zeta.Tests/Zeta.Tests.csproj");
        repo.Touch("tests/Sample.UnitTests/Sample.UnitTests.csproj");
        repo.Touch("tests/Sample.Tests/Sample.Tests.csproj");
        repo.Touch("tests/Sample.IntegrationTests/Sample.IntegrationTests.csproj");
        repo.Touch("src/Sample.Domain/Sample.Domain.csproj");
        repo.Touch("Stray.Tests.txt");

        var command = await CaptureCommand(repo, WriteStandardReport);

        var args = command.Arguments;
        var projects = Enumerable.Range(0, args.Count)
            .Where(i => args[i] == "--test-project")
            .Select(i => args[i + 1])
            .ToList();
        projects.Should().Equal(
            "tests/Sample.Tests/Sample.Tests.csproj",
            "tests/Sample.UnitTests/Sample.UnitTests.csproj",
            "tests/Zeta.Tests/Zeta.Tests.csproj");
    }

    [Fact]
    [Trait("scenario", "US3-AS24")]
    public async Task Cancellation_without_a_cancelled_token_rethrows_instead_of_reporting_budget()
    {
        using var repo = MutationDiffGateTests.TempRepo.Create();
        var runner = new StrykerRunner(new MutationDiffGateTests.FakeProcess((_, _) =>
            Task.FromException<ProcessResult>(new OperationCanceledException())));

        await FluentActions.Awaiting(() => runner.RunAsync(repo.Path, "origin/develop"))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("scenario", "US3-AS24")]
    public void Zero_minute_budget_cancels_the_diff_source()
    {
        var config = GatesConfig.Default with { Mutation = new MutationSettings(DiffBudgetMinutes: 0) };

        using var source = MutationBudget.CreateDiffSource(config);

        source.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)).Should().BeTrue();
        source.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    [Trait("scenario", "US3-AS10")]
    public void Offenders_are_sorted_by_file_then_line_regardless_of_input_order()
    {
        var mutants = new[]
        {
            new MutantResult(CoreFile, 20, "Linq", MutantStatus.Survived),
            new MutantResult("src/Sample.Domain/Abc.cs", 9, "Boolean", MutantStatus.Survived),
            new MutantResult(CoreFile, 3, "Arithmetic", MutantStatus.Survived),
        };

        var core = MutationDiffGate.Evaluate(mutants, Core, GatesConfig.Default, Policy.Blocking)[0];

        core.Verdict.Should().Be(Verdict.Fail);
        core.Offenders.Should().BeEquivalentTo(
            ["src/Sample.Domain/Abc.cs:9 Boolean", $"{CoreFile}:3 Arithmetic", $"{CoreFile}:20 Linq"],
            o => o.WithStrictOrdering());
    }

    [Fact]
    [Trait("scenario", "US3-AS13")]
    public void Skip_result_carries_the_exact_skip_message()
    {
        var results = MutationDiffGate.Evaluate([], Core, GatesConfig.Default, Policy.Skip);

        results[0].Message.Should().Be("skip: mutation testing is not enforced for this branch pair");
    }

    [Fact]
    [Trait("scenario", "US3-AS21")]
    public async Task Real_process_receives_its_arguments_and_reports_exit_code_and_output()
    {
        var result = await new SystemProcessRunner().RunAsync(
            new ProcessCommand("dotnet", ["--version"], Path.GetTempPath()), CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.OutputTail.Trim().Should().MatchRegex(@"^\d+\.\d+\.\d+");
    }

    [Fact]
    [Trait("scenario", "US3-AS23")]
    public async Task Output_tail_keeps_only_the_last_2000_characters()
    {
        var command = OperatingSystem.IsWindows()
            ? new ProcessCommand("powershell", ["-NoProfile", "-Command", "Write-Output ('x' * 3000 + 'END')"], Path.GetTempPath())
            : new ProcessCommand("sh", ["-c", "printf 'x%.0s' $(seq 1 3000); echo END"], Path.GetTempPath());

        var result = await new SystemProcessRunner().RunAsync(command, CancellationToken.None);

        result.OutputTail.Should().HaveLength(2000);
        result.OutputTail.TrimEnd().Should().EndWith("END");
        result.OutputTail.Should().StartWith("x");
    }

    [Fact]
    [Trait("scenario", "US3-AS23")]
    public async Task Short_output_is_returned_whole()
    {
        var command = OperatingSystem.IsWindows()
            ? new ProcessCommand("powershell", ["-NoProfile", "-Command", "Write-Output 'short'"], Path.GetTempPath())
            : new ProcessCommand("sh", ["-c", "echo short"], Path.GetTempPath());

        var result = await new SystemProcessRunner().RunAsync(command, CancellationToken.None);

        result.OutputTail.TrimEnd().Should().Be("short");
    }

    [Fact]
    [Trait("scenario", "US3-AS24")]
    public async Task Cancellation_kills_grandchildren_not_only_the_direct_child()
    {
        using var dir = MutationDiffGateTests.TempRepo.Create();
        var ready = Path.Combine(dir.Path, "ready.txt");
        var done = Path.Combine(dir.Path, "done.txt");
        ProcessCommand command;
        if (OperatingSystem.IsWindows())
        {
            var child = Path.Combine(dir.Path, "child.ps1");
            File.WriteAllText(child, $"New-Item -ItemType File -Path '{ready}' | Out-Null\nStart-Sleep -Seconds 6\nNew-Item -ItemType File -Path '{done}' | Out-Null\n");
            command = new ProcessCommand("powershell",
                ["-NoProfile", "-Command", $"Start-Process powershell -ArgumentList '-NoProfile','-File','{child}'; Start-Sleep -Seconds 120"],
                dir.Path);
        }
        else
        {
            var child = Path.Combine(dir.Path, "child.sh");
            File.WriteAllText(child, $"touch '{ready}'\nsleep 6\ntouch '{done}'\n");
            command = new ProcessCommand("sh", ["-c", $"sh '{child}' & sleep 120"], dir.Path);
        }

        using var cts = new CancellationTokenSource();
        var run = new SystemProcessRunner().RunAsync(command, cts.Token);
        for (var i = 0; i < 300 && !File.Exists(ready); i++)
        {
            await Task.Delay(100);
        }

        File.Exists(ready).Should().BeTrue();

        cts.Cancel();
        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        await Task.Delay(8000);

        File.Exists(done).Should().BeFalse();
    }
}
