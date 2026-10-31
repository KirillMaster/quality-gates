using System.CommandLine;

namespace KirillMaster.QualityGates.Cli.Commands;

internal static class CommandOptions
{
    public static Option<string> Required(string name, string description) => new(name) { Required = true, Description = description };

    public static Option<string[]> RequiredMany(string name, string description) =>
        new(name) { Required = true, Description = description, AllowMultipleArgumentsPerToken = true };

    public static Command Create(string name, string description, Func<string, int> handler, params Option[] options)
    {
        var command = new Command(name, description);
        foreach (var option in options)
        {
            command.Options.Add(option);
        }

        command.SetAction(_ => handler(name));
        return command;
    }
}
