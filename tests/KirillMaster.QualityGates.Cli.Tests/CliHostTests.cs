using FluentAssertions;
using KirillMaster.QualityGates.Cli;
using KirillMaster.QualityGates.Cli.Application;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class CliHostTests
{
    private static (int Code, string Out, string Err) Run(string[] args, Func<string, int>? handler = null)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = CliHost.Run(args, o, e, handler);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    [Trait("scenario", "US2-AS17")]
    public void Help_lists_the_four_modes_and_exits_zero()
    {
        var (code, output, _) = Run(["--help"]);

        code.Should().Be(0);
        var lines = output.Split('\n').Select(l => l.Trim()).ToList();
        foreach (var mode in new[] { "pr", "mutation-diff", "nightly", "audit" })
        {
            lines.Should().Contain(l => l == mode || l.StartsWith(mode + " ", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS17")]
    public void Version_exits_zero() => Run(["--version"]).Code.Should().Be(0);

    [Theory]
    [Trait("scenario", "US2-AS18")]
    [InlineData("pass", 0)]
    [InlineData("pass,skip,notapplicable", 0)]
    [InlineData("pass,warn", 2)]
    [InlineData("timeout", 2)]
    [InlineData("pass,fail", 1)]
    [InlineData("fail,warn", 1)]
    public void Exit_code_mapping(string verdicts, int expected)
    {
        var results = verdicts.Split(',')
            .Select(v => new GateResult(GateId.DiffCoverage, Enum.Parse<Verdict>(v, true)))
            .ToList();
        var report = new RunReport(RunMode.Pr, Policy.Blocking, results, 0);

        ExitCodeMapper.Map(report).Should().Be(expected);
    }

    [Fact]
    [Trait("scenario", "US2-AS19")]
    public void Unhandled_exception_maps_to_three_and_writes_stderr()
    {
        var (code, _, err) = Run(
            ["pr", "--repo", ".", "--target", "origin/develop", "--source", "f", "--coverage", "c.xml", "--sarif", "s.sarif", "--out", "o"],
            _ => throw new InvalidOperationException("boom"));

        code.Should().Be(3);
        err.Should().Contain("boom");
    }

    [Fact]
    [Trait("scenario", "US2-AS19")]
    public void Handler_exit_code_is_returned_when_no_exception()
    {
        var (code, _, _) = Run(["audit", "--repo", ".", "--out", "o"], _ => 2);

        code.Should().Be(2);
    }

    [Theory]
    [Trait("scenario", "US2-AS58")]
    [InlineData("bogus-mode")]
    [InlineData("pr --no-such-option x")]
    [InlineData("pr --repo . --target origin/develop")]
    [InlineData("pr --repo")]
    public void Usage_errors_exit_three_with_message_on_stderr(string args)
    {
        var (code, _, err) = Run(args.Split(' '), _ => throw new InvalidOperationException("handler must not run"));

        code.Should().Be(3);
        err.Should().NotBeNullOrWhiteSpace().And.NotContain("handler must not run");
    }

    public static TheoryData<string, string[]> HelpFragments => new()
    {
        {
            "pr",
            ["Run the pull-request gates", "Repo root", "Target branch (origin/develop)", "Source branch name",
             "Cobertura files (unit + integration globs)", "SARIF files glob", "Default from gates.json",
             "Output dir for quality-report.json + html"]
        },
        {
            "mutation-diff",
            ["Run mutation testing on the diff", "Repo root", "Target branch", "Source branch (branch matrix input)",
             "Output dir; writes mutation-verdict.json"]
        },
        {
            "nightly",
            ["Run the nightly ratchet on develop", "Repo root on develop", "Output dir",
             "Open baseline PR when improved (default true)"]
        },
        {
            "audit",
            ["Produce the hotspot audit report", "Repo root", "Output dir: hotspots.json, hotspots.html",
             "Run full mutation (slow) or reuse nightly results"]
        }
    };

    [Theory]
    [Trait("scenario", "US2-AS17")]
    [MemberData(nameof(HelpFragments))]
    public void Subcommand_help_shows_its_description_and_option_descriptions(string mode, string[] fragments)
    {
        var (code, output, _) = Run([mode, "--help"]);

        code.Should().Be(0);
        var normalized = string.Join(' ', output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var fragment in fragments)
        {
            normalized.Should().Contain(fragment);
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS17")]
    public void Root_help_shows_the_tool_description()
    {
        var (_, output, _) = Run(["--help"]);

        output.Should().Contain("Quality gates for .NET repositories");
    }

    public static TheoryData<string[], string> ValidInvocations => new()
    {
        { ["pr", "--repo", ".", "--target", "origin/develop", "--source", "f", "--coverage", "c.xml", "--sarif", "s.sarif", "--out", "o"], "pr" },
        { ["pr", "--repo", ".", "--target", "origin/develop", "--source", "f", "--coverage", "a.xml", "b.xml", "--sarif", "s1.sarif", "s2.sarif", "--integration-enabled", "--out", "o"], "pr" },
        { ["mutation-diff", "--repo", ".", "--target", "origin/develop", "--source", "f", "--out", "o"], "mutation-diff" },
        { ["nightly", "--repo", ".", "--out", "o"], "nightly" },
        { ["nightly", "--repo", ".", "--out", "o", "--open-pr"], "nightly" },
        { ["audit", "--repo", ".", "--out", "o"], "audit" },
        { ["audit", "--repo", ".", "--out", "o", "--with-mutation"], "audit" }
    };

    [Theory]
    [Trait("scenario", "US2-AS17")]
    [MemberData(nameof(ValidInvocations))]
    public void Valid_invocation_dispatches_the_named_mode_and_returns_handler_code(string[] args, string expectedMode)
    {
        var calls = new List<string>();

        var code = CliHost.Run(args, new StringWriter(), new StringWriter(), mode =>
        {
            calls.Add(mode);
            return 0;
        });

        code.Should().Be(0);
        calls.Should().Equal(expectedMode);
    }

    [Theory]
    [Trait("scenario", "US2-AS58")]
    [InlineData("pr --repo . --target origin/develop --source f --sarif s.sarif --out o")]
    [InlineData("pr --repo . --target origin/develop --source f --coverage c.xml --out o")]
    [InlineData("pr --repo . --target origin/develop --source f --coverage c.xml --sarif s.sarif")]
    [InlineData("pr --repo . --source f --coverage c.xml --sarif s.sarif --out o")]
    [InlineData("pr --target origin/develop --source f --coverage c.xml --sarif s.sarif --out o")]
    [InlineData("mutation-diff --repo . --target origin/develop --out o")]
    [InlineData("mutation-diff --repo . --source f --out o")]
    [InlineData("mutation-diff --target origin/develop --source f --out o")]
    [InlineData("nightly --out o")]
    [InlineData("nightly --repo .")]
    [InlineData("audit --out o")]
    [InlineData("audit --repo .")]
    public void Missing_required_option_exits_three_without_running_handler(string args)
    {
        var calls = new List<string>();

        var code = CliHost.Run(args.Split(' '), new StringWriter(), new StringWriter(), mode =>
        {
            calls.Add(mode);
            return 0;
        });

        code.Should().Be(3);
        calls.Should().BeEmpty();
    }

    [Fact]
    [Trait("scenario", "US2-AS19")]
    public void Mode_without_handler_reports_not_implemented_on_stderr()
    {
        var stderr = new StringWriter();

        var code = CliHost.Run(["audit", "--repo", ".", "--out", "o"], new StringWriter(), stderr);

        code.Should().Be(3);
        stderr.ToString().Should().Contain("mode 'audit' is not implemented yet");
    }
}
