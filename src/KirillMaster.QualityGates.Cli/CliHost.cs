using System.CommandLine;
using KirillMaster.QualityGates.Cli.Application;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Commands;

namespace KirillMaster.QualityGates.Cli;

public static class CliHost
{
    /// <summary>
    /// Runs the CLI. <paramref name="handlerOverride"/> replaces the mode handlers (test seam);
    /// it receives the subcommand name and returns the exit code.
    /// </summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, Func<string, int>? handlerOverride = null) =>
        Run(args, stdout, stderr, handlerOverride, null);

    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, Func<string, int>? handlerOverride, IMutationRunner? mutationRunner)
    {
        var handler = handlerOverride ?? NotImplemented(stderr);
        var root = new RootCommand("Quality gates for .NET repositories")
        {
            PrCommand.Build(handler, handlerOverride is null ? stdout : null, handlerOverride is null ? stderr : null),
            MutationDiffCommand.Build(handler, handlerOverride is null ? stdout : null, handlerOverride is null ? stderr : null, mutationRunner),
            MutationRatchetCommand.Build(handler, handlerOverride is null ? stdout : null, handlerOverride is null ? stderr : null),
            NightlyCommand.Build(handler),
            AuditCommand.Build(handler),
            GrandfatherCommand.Build(handler, handlerOverride is null ? stdout : null),
            BaselineCommand.Build(handler, handlerOverride is null ? stdout : null)
        };

        try
        {
            var parse = root.Parse(args);
            if (parse.Errors.Count > 0)
            {
                foreach (var error in parse.Errors)
                {
                    stderr.WriteLine(error.Message);
                }

                return ExitCodeMapper.Error;
            }

            return parse.Invoke(new InvocationConfiguration
            {
                Output = stdout,
                Error = stderr,
                EnableDefaultExceptionHandler = false
            });
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex.Message);
            return ExitCodeMapper.Error;
        }
    }

    private static Func<string, int> NotImplemented(TextWriter stderr) => mode =>
    {
        stderr.WriteLine($"mode '{mode}' is not implemented yet");
        return ExitCodeMapper.Error;
    };
}
