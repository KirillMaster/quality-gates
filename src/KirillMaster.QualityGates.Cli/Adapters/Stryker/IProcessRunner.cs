using System.Diagnostics;

namespace KirillMaster.QualityGates.Cli.Adapters.Stryker;

public sealed record ProcessCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>Exit code plus the tail of the combined output; the output is diagnostics only and never scored.</summary>
public sealed record ProcessResult(int ExitCode, string OutputTail = "");

/// <summary>Process seam: runs a command to completion and returns its exit code; cancellation kills the whole process tree.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(command.FileName)
        {
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in command.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {command.FileName}");
        var drain = Task.WhenAll(
            process.StandardOutput.ReadToEndAsync(CancellationToken.None),
            process.StandardError.ReadToEndAsync(CancellationToken.None));
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var output = await drain.ConfigureAwait(false);
        var combined = string.Concat(output);
        return new ProcessResult(process.ExitCode, combined.Length > 2000 ? combined[^2000..] : combined);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
