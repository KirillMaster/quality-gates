namespace KirillMaster.QualityGates.Cli.Application.Ports;

public interface IGitClient
{
    /// <summary>Returns the output of <c>git diff -U0 {targetBranch}...HEAD</c> for the repository.</summary>
    Task<string> DiffAsync(string repoPath, string targetBranch, CancellationToken cancellationToken = default);

    /// <summary>Returns the commit hash of HEAD.</summary>
    Task<string> HeadAsync(string repoPath, CancellationToken cancellationToken = default);
}
