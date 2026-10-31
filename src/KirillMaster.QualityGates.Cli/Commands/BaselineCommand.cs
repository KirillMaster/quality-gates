using System.CommandLine;
using KirillMaster.QualityGates.Cli.Adapters.Json;
using KirillMaster.QualityGates.Cli.Adapters.Pr;
using KirillMaster.QualityGates.Cli.Application;
using KirillMaster.QualityGates.Cli.Application.UseCases;
using KirillMaster.QualityGates.Cli.Domain.Gates;

namespace KirillMaster.QualityGates.Cli.Commands;

internal static class BaselineCommand
{
    public const string Name = "baseline";
    public const string MergeNewName = "merge-new";
    public const string FanOutName = "fan-out";

    public static Command Build(Func<string, int> handler, TextWriter? stdout = null)
    {
        var repo = CommandOptions.Required("--repo", "Repo root holding quality/baseline.json");
        var sarif = CommandOptions.RequiredMany("--sarif", "SARIF files glob");

        var mergeNew = new Command(MergeNewName, "Add counts for (project, rule) pairs absent from the baseline; existing values stay untouched");
        mergeNew.Options.Add(repo);
        mergeNew.Options.Add(sarif);
        mergeNew.SetAction(parse =>
        {
            if (stdout is null)
            {
                return handler($"{Name} {MergeNewName}");
            }

            var runner = new BaselineMergeRunner(new JsonBaselineStore(), new SarifSource(), new GlobFiles());
            var result = runner.MergeNew(Path.GetFullPath(parse.GetValue(repo)!), parse.GetValue(sarif)!);
            stdout.WriteLine($"added {result.Added} new (project, rule) entries to {result.Path}");
            return ExitCodeMapper.Success;
        });

        var fanOutRepo = CommandOptions.Required("--repo", "Repo root holding quality/baseline.json and the csproj files");
        var limit = new Option<int>("--fanout-limit") { Description = "Projects with more transitive project references than this are recorded", DefaultValueFactory = _ => TransitiveFanOutGate.DefaultLimit };
        var fanOut = new Command(FanOutName, "Record the transitive fan-out of projects over the limit in the baseline fanOut section; recorded values only shrink");
        fanOut.Options.Add(fanOutRepo);
        fanOut.Options.Add(limit);
        fanOut.SetAction(parse =>
        {
            if (stdout is null)
            {
                return handler($"{Name} {FanOutName}");
            }

            var runner = new FanOutBaselineRunner(new JsonBaselineStore(), new GlobFiles());
            var result = runner.Update(Path.GetFullPath(parse.GetValue(fanOutRepo)!), parse.GetValue(limit));
            stdout.WriteLine($"fanOut in {result.Path}: {result.Added} added, {result.Lowered} lowered, {result.Removed} removed");
            return ExitCodeMapper.Success;
        });

        var command = new Command(Name, "Maintain quality/baseline.json");
        command.Subcommands.Add(mergeNew);
        command.Subcommands.Add(fanOut);
        return command;
    }
}
