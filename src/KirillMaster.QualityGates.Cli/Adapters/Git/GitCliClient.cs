using System.Diagnostics;
using System.Text;
using KirillMaster.QualityGates.Cli.Application.Ports;

namespace KirillMaster.QualityGates.Cli.Adapters.Git;

public sealed class GitCliClient : IGitClient
{
    public Task<string> DiffAsync(string repoPath, string targetBranch, CancellationToken cancellationToken = default) =>
        RunAsync(repoPath, ["diff", "-U0", "-M", "--no-color", "--no-ext-diff", $"{targetBranch}...HEAD"], $"git diff {targetBranch}...HEAD", cancellationToken);

    public async Task<string> HeadAsync(string repoPath, CancellationToken cancellationToken = default) =>
        (await RunAsync(repoPath, ["rev-parse", "HEAD"], "git rev-parse HEAD", cancellationToken).ConfigureAwait(false)).Trim();

    internal static async Task<string> RunAsync(string repoPath, string[] args, string description, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["LC_ALL"] = "C";
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{description} failed ({process.ExitCode}): {error.Trim()}");
        }

        return output;
    }
}
