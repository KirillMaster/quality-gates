using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using KirillMaster.QualityGates.Cli.Adapters.Pr;
using KirillMaster.QualityGates.Cli.Adapters.Stryker;
using KirillMaster.QualityGates.Cli.Application;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Commands;

/// <summary>Y14: reports the nightly Stryker mutation score against quality/mutation-baseline.json. A drop warns (exit 2); an unreadable report or baseline is a tool error (exit 3). A better score is written to the output directory as the proposed baseline.</summary>
internal static class MutationRatchetCommand
{
    public const string Name = "mutation-ratchet";
    public const string BaselineFile = "mutation-baseline.json";

    public static Command Build(Func<string, int> handler, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var repo = CommandOptions.Required("--repo", "Repo root");
        var report = CommandOptions.Required("--report", "Stryker mutation-report.json");
        var output = CommandOptions.Required("--out", "Output dir for mutation-ratchet.json and the proposed mutation-baseline.json");
        var baseline = new Option<string?>("--baseline") { Description = "Committed baseline (default: <repo>/quality/mutation-baseline.json)" };

        var command = new Command(Name, "Report the mutation score against the committed baseline");
        foreach (var option in new Option[] { repo, report, output, baseline })
        {
            command.Options.Add(option);
        }

        command.SetAction(parse =>
        {
            if (stdout is null || stderr is null)
            {
                return handler(Name);
            }

            var repoPath = Path.GetFullPath(parse.GetValue(repo)!);
            var outDir = Path.GetFullPath(parse.GetValue(output)!);
            var baselinePath = parse.GetValue(baseline) is { Length: > 0 } custom ? Path.GetFullPath(custom) : Path.Combine(repoPath, "quality", BaselineFile);
            try
            {
                var mutants = MutationReportParser.Parse(File.ReadAllText(parse.GetValue(report)!), repoPath);
                var previous = File.Exists(baselinePath) ? ReadScore(baselinePath) : (double?)null;
                var result = MutationRatchetGate.Evaluate(mutants, previous);
                var run = new RunReport(RunMode.Nightly, Policy.ReportOnly, [result], 0);
                run = run with { ExitCode = ExitCodeMapper.Map(run) };
                new JsonReportStore().Write(outDir, run, "mutation-ratchet.json");
                if (result.Actual is { } score && result.Verdict == Verdict.Pass && (previous is null || score > previous))
                {
                    File.WriteAllText(Path.Combine(outDir, BaselineFile), JsonSerializer.Serialize(new Dictionary<string, double> { ["score"] = score }));
                }

                ReportPrinter.Print(run, stdout, stderr);
                return run.ExitCode;
            }
            catch (Exception ex) when (ex is IOException or JsonException or MutationReportException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                stderr.WriteLine($"mutation-ratchet could not run: {ex.Message}");
                return ExitCodeMapper.Error;
            }
        });

        return command;
    }

    private static double ReadScore(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("score").GetDouble();
    }
}
