using System.Diagnostics;
using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Git;
using KirillMaster.QualityGates.Cli.Domain.Diff;

namespace KirillMaster.QualityGates.Cli.IntegrationTests;

public class GitCliClientTests
{
    [Fact]
    [Trait("scenario", "US2-AS27")]
    public async Task Git_client_produces_U0_diff_against_target_branch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qg-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Git(dir, "init", "-q", "-b", "develop");
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, string.Join("\n", Enumerable.Range(1, 10).Select(i => $"// line {i}")) + "\n");
            Git(dir, "add", ".");
            Git(dir, "commit", "-q", "-m", "base");
            Git(dir, "checkout", "-q", "-b", "feature");
            var lines = File.ReadAllLines(file);
            lines[2] = "// changed 3";
            lines[6] = "// changed 7";
            File.WriteAllText(file, string.Join("\n", lines) + "\n");
            Git(dir, "commit", "-q", "-am", "change");

            var diff = await new GitCliClient().DiffAsync(dir, "develop");
            var parsed = DiffParser.Parse(diff);

            parsed.Files.Should().BeEquivalentTo(["A.cs"]);
            parsed.LinesOf("A.cs").Should().Equal(3, 7);
        }
        finally
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                f.Attributes = FileAttributes.Normal;
            }

            Directory.Delete(dir, true);
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
