using System.CommandLine;

namespace KirillMaster.QualityGates.Cli.Commands;

internal static class AuditCommand
{
    public static Command Build(Func<string, int> handler) => CommandOptions.Create(
        "audit",
        "Produce the hotspot audit report",
        handler,
        CommandOptions.Required("--repo", "Repo root"),
        CommandOptions.Required("--out", "Output dir: hotspots.json, hotspots.html"),
        new Option<bool?>("--with-mutation") { Description = "Run full mutation (slow) or reuse nightly results" });
}
