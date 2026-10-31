using System.Diagnostics;
using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Git;
using KirillMaster.QualityGates.Cli.Application.Ports;

namespace KirillMaster.QualityGates.Cli.IntegrationTests;

public sealed class GitPrChangeSourceTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "qg-pcs-" + Guid.NewGuid().ToString("N"));

    public GitPrChangeSourceTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "develop");
        File.WriteAllText(Path.Combine(_repo, "A.txt"), "a\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "base");
        Git("update-ref", "refs/remotes/origin/develop", "HEAD");
        Git("checkout", "-q", "-b", "feature/x");
    }

    public void Dispose()
    {
        foreach (var file in new DirectoryInfo(_repo).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes = FileAttributes.Normal;
        }

        Directory.Delete(_repo, true);
    }

    [Fact]
    [Trait("scenario", "RES-14572")]
    public async Task Existing_file_is_read_and_a_missing_path_is_null_at_every_revision()
    {
        var source = new GitPrChangeSource();

        (await source.ReadAsync(_repo, "origin/develop", PrRevision.Head, "A.txt")).Should().Be("a\n");
        (await source.ReadAsync(_repo, "origin/develop", PrRevision.Base, "A.txt")).Should().Be("a\n");
        (await source.ReadAsync(_repo, "origin/develop", PrRevision.Target, "A.txt")).Should().Be("a\n");
        (await source.ReadAsync(_repo, "origin/develop", PrRevision.Head, "nope/B.txt")).Should().BeNull();
        (await source.ReadAsync(_repo, "origin/develop", PrRevision.Target, ".github/CODEOWNERS")).Should().BeNull();
    }

    [Fact]
    [Trait("scenario", "RES-14572")]
    public async Task Batch_read_returns_blobs_nulls_and_non_ascii_text_with_one_git_process()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "\u00dcber.cs"), "class \u00dc { }\r\nline2\n");
        File.WriteAllText(Path.Combine(_repo, "empty.txt"), string.Empty);
        Git("add", "-A");
        Git("commit", "-q", "-m", "more");
        var source = new GitPrChangeSource();

        var head = await source.ReadManyAsync(_repo, "origin/develop", PrRevision.Head, ["A.txt", "src/\u00dcber.cs", "empty.txt", "nope.txt", "A.txt"]);
        var onBase = await source.ReadManyAsync(_repo, "origin/develop", PrRevision.Base, ["A.txt", "empty.txt"]);

        head.Should().HaveCount(4);
        head["A.txt"].Should().Be("a\n");
        head["src/\u00dcber.cs"].Should().Be("class \u00dc { }\r\nline2\n");
        head["empty.txt"].Should().BeEmpty();
        head["nope.txt"].Should().BeNull();
        onBase["A.txt"].Should().Be("a\n");
        onBase["empty.txt"].Should().BeNull();
        (await source.ReadManyAsync(_repo, "origin/develop", PrRevision.Head, [])).Should().BeEmpty();
        var unknown = () => source.ReadManyAsync(_repo, "origin/nope", PrRevision.Target, ["A.txt"]);
        await unknown.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("scenario", "RES-14572")]
    public async Task Base_files_and_diff_from_commit_use_the_merge_base_not_head()
    {
        var mergeBase = Git("rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(_repo, "B.txt"), "line one\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "feature");
        var source = new GitPrChangeSource();

        (await source.BaseFilesAsync(_repo, "origin/develop")).Should().Equal("A.txt");
        (await source.DiffFromAsync(_repo, mergeBase)).Should().Contain("+++ b/B.txt").And.Contain("+line one");
        (await source.DiffFromAsync(_repo, "HEAD")).Should().BeEmpty();
    }

    [Fact]
    [Trait("scenario", "RES-14572")]
    public async Task Untracked_file_that_exists_on_disk_is_missing_from_the_revision()
    {
        File.WriteAllText(Path.Combine(_repo, "untracked.txt"), "x\n");

        (await new GitPrChangeSource().ReadAsync(_repo, "origin/develop", PrRevision.Head, "untracked.txt")).Should().BeNull();
    }

    [Fact]
    [Trait("scenario", "RES-14572")]
    public async Task Unknown_target_is_a_tool_error_not_a_missing_file()
    {
        var source = new GitPrChangeSource();

        var onTarget = () => source.ReadAsync(_repo, "origin/nope", PrRevision.Target, "A.txt");
        var onBase = () => source.ReadAsync(_repo, "origin/nope", PrRevision.Base, "A.txt");

        await onTarget.Should().ThrowAsync<InvalidOperationException>();
        await onBase.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("scenario", "RES-14572")]
    public async Task Broken_repository_is_a_tool_error()
    {
        var notARepo = Path.Combine(_repo, "plain");
        Directory.CreateDirectory(notARepo);
        File.WriteAllText(Path.Combine(notARepo, ".git"), "not a git dir");

        var read = () => new GitPrChangeSource().ReadAsync(notARepo, "origin/develop", PrRevision.Head, "A.txt");

        await read.Should().ThrowAsync<InvalidOperationException>();
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(args))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, err);
        return stdout;
    }
}
