using System.Globalization;
using KirillMaster.QualityGates.Cli.Application.Ports;

namespace KirillMaster.QualityGates.Cli.Adapters.Git;

public sealed class GitBranchInfo : IBranchInfo
{
    public async Task<DateTimeOffset?> FirstBranchCommitAsync(string repoPath, string target, CancellationToken cancellationToken = default)
    {
        var output = await GitCliClient.RunAsync(repoPath, ["log", "--reverse", "--format=%cI", $"{target}..HEAD"], $"git log {target}..HEAD", cancellationToken).ConfigureAwait(false);
        var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return first is not null && DateTimeOffset.TryParse(first, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
    }
}
