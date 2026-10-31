using System.CommandLine;

namespace KirillMaster.QualityGates.Cli.Commands;

internal static class NightlyCommand
{
    public static Command Build(Func<string, int> handler) => CommandOptions.Create(
        "nightly",
        "Run the nightly ratchet on develop",
        handler,
        CommandOptions.Required("--repo", "Repo root on develop"),
        CommandOptions.Required("--out", "Output dir"),
        new Option<bool?>("--open-pr") { Description = "Open baseline PR when improved (default true)" });
}
