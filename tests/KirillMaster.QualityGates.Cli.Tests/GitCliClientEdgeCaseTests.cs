using System.Diagnostics;
using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Git;

namespace KirillMaster.QualityGates.Cli.IntegrationTests;

public class GitCliClientEdgeCaseTests
{
    [Fact]
    [Trait("scenario", "US2-AS27")]
    public async Task Git_failure_for_unknown_target_branch_is_reported_as_an_error()
    {
        var dir = NewRepo();
        try
        {
            await FluentActions.Awaiting(() => new GitCliClient().DiffAsync(dir, "missing-branch"))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("git diff missing-branch...HEAD failed (*");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS27")]
    public async Task Cancelled_token_stops_the_git_diff_call()
    {
        var dir = NewRepo();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await FluentActions.Awaiting(() => new GitCliClient().DiffAsync(dir, "develop", cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string NewRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qg-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "develop");
        File.WriteAllText(Path.Combine(dir, "A.cs"), "// base\n");
        Git(dir, "add", ".");
        Git(dir, "commit", "-q", "-m", "base");
        return dir;
    }

    private static void Cleanup(string dir)
    {
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            f.Attributes = FileAttributes.Normal;
        }

        for (var attempt = 1; attempt <= 50; attempt++)
        {
            try
            {
                Directory.Delete(dir, true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(args))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, err);
    }
}
