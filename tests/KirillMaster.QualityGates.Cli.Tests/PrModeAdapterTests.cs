using FluentAssertions;
using KirillMaster.QualityGates.Cli;
using KirillMaster.QualityGates.Cli.Adapters.Pr;

namespace KirillMaster.QualityGates.Cli.Tests;

public sealed class PrModeAdapterTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qg-glob-" + Guid.NewGuid().ToString("N"));

    public PrModeAdapterTests() => Directory.CreateDirectory(_tmp);

    public void Dispose() => Directory.Delete(_tmp, true);

    [Fact]
    [Trait("scenario", "US2-AS47")]
    public void Glob_expands_wildcards_and_ignores_misses()
    {
        File.WriteAllText(Path.Combine(_tmp, "a.xml"), "<a/>");
        File.WriteAllText(Path.Combine(_tmp, "b.txt"), "b");
        var files = new GlobFiles();

        files.Expand([Path.Combine(_tmp, "*.xml")]).Should().ContainSingle().Which.Should().EndWith("a.xml");
        files.Expand([Path.Combine(_tmp, "none", "*.xml")]).Should().BeEmpty();
        files.Exists(Path.Combine(_tmp, "b.txt")).Should().BeTrue();
    }

    [Fact]
    [Trait("scenario", "US2-AS51")]
    public void Unknown_skip_group_is_an_input_error()
    {
        var stderr = new StringWriter();

        var exit = CliHost.Run(
            ["pr", "--repo", _tmp, "--target", "origin/develop", "--source", "x", "--coverage", "c", "--sarif", "s", "--out", _tmp, "--skip", "bogus"],
            new StringWriter(),
            stderr);

        exit.Should().Be(3);
    }
}
