using System.CommandLine;
using KirillMaster.QualityGates.Cli.Adapters.Grandfather;
using KirillMaster.QualityGates.Cli.Adapters.Json;
using KirillMaster.QualityGates.Cli.Adapters.Roslyn;
using KirillMaster.QualityGates.Cli.Application;
using KirillMaster.QualityGates.Cli.Application.UseCases;

namespace KirillMaster.QualityGates.Cli.Commands;

internal static class GrandfatherCommand
{
    public const string Name = "grandfather";

    public static Command Build(Func<string, int> handler, TextWriter? stdout = null)
    {
        var repo = CommandOptions.Required("--repo", "Repository root to scan for banned (RS0030) call sites.");
        var sarif = new Option<string[]>("--sarif")
        {
            Description = "Use these SARIF files/directories instead of running a scan build.",
            AllowMultipleArgumentsPerToken = true
        };

        var qualityRoot = new Option<string?>("--quality-root")
        {
            Description = "Directory holding quality/ (the build's QualityGatesRoot, the folder of Directory.Build.props); defaults to --repo."
        };

        var command = new Command(Name, "Grandfather existing RS0030 call sites into quality/grandfathered (EC-7).");
        command.Options.Add(repo);
        command.Options.Add(qualityRoot);
        command.Options.Add(sarif);

        command.SetAction(parse =>
        {
            if (stdout is null)
            {
                return handler(Name);
            }

            var root = Path.GetFullPath(parse.GetValue(repo)!);
            var runner = new GrandfatherRunner(new RoslynBannedCallScanner(), new GrandfatherFiles(), new JsonBaselineStore());
            var result = runner.Run(root, parse.GetValue(sarif) ?? [], DateOnly.FromDateTime(DateTime.UtcNow), parse.GetValue(qualityRoot) is { } quality ? Path.GetFullPath(quality) : null);
            stdout.WriteLine($"grandfathered {result.Sites} call site(s) in {result.Projects.Count} project(s)");
            return ExitCodeMapper.Success;
        });

        return command;
    }
}
