using System.Diagnostics;
using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Git;

namespace KirillMaster.QualityGates.Cli.IntegrationTests;

public class GitCliClientHeadTests
{
    [Fact]
    [Trait("scenario", "US2-AS50")]
    public async Task Head_returns_trimmed_full_commit_hash_matching_git()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qg-head-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Run(dir, "init", "-q", "-b", "develop");
            File.WriteAllText(Path.Combine(dir, "A.cs"), "// base\n");
            Run(dir, "add", ".");
            Run(dir, "commit", "-q", "-m", "base");
            var expected = Run(dir, "rev-parse", "HEAD").Trim();

            var head = await new GitCliClient().HeadAsync(dir);

            head.Should().Be(expected);
            head.Should().MatchRegex("^[0-9a-f]{40}$");
        }
        finally
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                f.Attributes = FileAttributes.Normal;
            }

            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS51")]
    public async Task Head_outside_a_repository_is_reported_as_an_error()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qg-nohead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await FluentActions.Awaiting(() => new GitCliClient().HeadAsync(dir))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("git rev-parse HEAD failed (*");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static string Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false" }.Concat(args))
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["GIT_CEILING_DIRECTORIES"] = Path.GetTempPath();
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, e);
        return o;
    }
}
