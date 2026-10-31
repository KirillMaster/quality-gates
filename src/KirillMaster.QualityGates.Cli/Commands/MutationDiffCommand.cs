using System.CommandLine;
using KirillMaster.QualityGates.Cli.Adapters.Git;
using KirillMaster.QualityGates.Cli.Adapters.Json;
using KirillMaster.QualityGates.Cli.Adapters.Pr;
using KirillMaster.QualityGates.Cli.Adapters.Stryker;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Application.UseCases;

namespace KirillMaster.QualityGates.Cli.Commands;

internal static class MutationDiffCommand
{
    public const string Name = "mutation-diff";

    public static Command Build(Func<string, int> handler, TextWriter? stdout = null, TextWriter? stderr = null, IMutationRunner? mutationRunner = null)
    {
        var repo = CommandOptions.Required("--repo", "Repo root");
        var target = CommandOptions.Required("--target", "Target branch");
        var source = CommandOptions.Required("--source", "Source branch (branch matrix input)");
        var output = CommandOptions.Required("--out", "Output dir; writes mutation-verdict.json");
        var skipOptions = new SkipOptions();

        var command = new Command(Name, "Run mutation testing on the diff");
        foreach (var option in new Option[] { repo, target, source, output })
        {
            command.Options.Add(option);
        }

        skipOptions.AddTo(command);

        command.SetAction(parse =>
        {
            if (stdout is null || stderr is null)
            {
                return handler(Name);
            }

            var (groups, bypassedBy) = skipOptions.Read(parse);
            var request = new MutationDiffRequest(
                Path.GetFullPath(parse.GetValue(repo)!),
                parse.GetValue(target)!,
                parse.GetValue(source)!,
                Path.GetFullPath(parse.GetValue(output)!),
                groups,
                bypassedBy);

            var runner = new MutationDiffRunner(
                new JsonConfigStore(),
                new GitCliClient(),
                new CsprojProjectLocator(),
                new GlobFiles(),
                mutationRunner ?? new StrykerRunner(),
                new JsonMutationVerdictStore());
            var outcome = runner.RunAsync(request).GetAwaiter().GetResult();
            ReportPrinter.Print(outcome.Report, stdout, stderr);
            return outcome.ExitCode;
        });

        return command;
    }
}
