using FluentAssertions;
using KirillMaster.QualityGates.Cli;
using KirillMaster.QualityGates.Cli.Adapters.Grandfather;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Application.UseCases;
using KirillMaster.QualityGates.Cli.Domain.Grandfather;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class GrandfatherTests
{
    private static readonly DateOnly Date = new(2026, 10, 21);
    private const string LoadId = "M:Sample.Adapters.Repo.Load(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade)";

    private sealed class FakeScanner(params BannedCallSite[] sites) : IBannedCallScanner
    {
        public IReadOnlyList<BannedCallSite> Scan(string repoRoot, IReadOnlyList<string> sarifPaths) => sites;
    }

    private sealed class FakeFiles(params string[] existing) : IGrandfatherFiles
    {
        public Dictionary<string, string> Written { get; } = [];
        public List<string> Deleted { get; } = [];

        public IReadOnlyList<string> ExistingProjects(string repoRoot) => existing;

        public void Write(string repoRoot, string project, string content) => Written[project] = content;

        public void Delete(string repoRoot, string project) => Deleted.Add(project);
    }

    private sealed class FakeBaselines(Baseline? loaded) : IBaselineStore
    {
        public Baseline? Saved { get; private set; }
        public string? SavedPath { get; private set; }

        public Baseline? Load(string path) => loaded;

        public void Save(string path, Baseline baseline)
        {
            Saved = baseline;
            SavedPath = path;
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Render_writes_member_scoped_ruled_suppression_with_date()
    {
        var text = GrandfatherSuppressions.Render([LoadId], Date);

        text.Should().Contain("[assembly: SuppressMessage(\"ApiDesign\", \"RS0030\"");
        text.Should().Contain("Scope = \"member\"");
        text.Should().Contain($"Target = \"~{LoadId}\"");
        text.Should().Contain("Justification = \"grandfathered 2026-10-21");
        text.Should().Contain("using System.Diagnostics.CodeAnalysis;");
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Render_sorts_targets_and_is_deterministic()
    {
        var text = GrandfatherSuppressions.Render(["M:B.Z", "M:A.Y"], Date);

        text.IndexOf("~M:A.Y", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("~M:B.Z", StringComparison.Ordinal));
        GrandfatherSuppressions.Render(["M:B.Z", "M:A.Y"], Date).Should().Be(text);
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Render_emits_one_attribute_per_duplicate_member_id()
    {
        var text = GrandfatherSuppressions.Render([LoadId, LoadId], Date);

        text.Split("[assembly:").Length.Should().Be(2, "duplicate member ids collapse to one attribute");
    }

    [Fact]
    [Trait("scenario", "US2-AS45")]
    public void Runner_saves_the_baseline_under_the_quality_directory()
    {
        var baselines = new FakeBaselines(new Baseline("c", "t", [], new Dictionary<string, IReadOnlyDictionary<string, int>>()));

        new GrandfatherRunner(new FakeScanner(new BannedCallSite("Sample.Adapters", LoadId)), new FakeFiles(), baselines)
            .Run("/repo", [], Date);

        baselines.SavedPath.Should().Be(Path.Combine("/repo", "quality", "baseline.json"));
    }

    [Fact]
    [Trait("scenario", "US2-AS45")]
    public void Runner_writes_files_and_baseline_under_the_quality_root_when_given()
    {
        var files = new RootRecordingFiles();
        var baselines = new FakeBaselines(new Baseline("c", "t", [], new Dictionary<string, IReadOnlyDictionary<string, int>>()));

        new GrandfatherRunner(new FakeScanner(new BannedCallSite("Sample.Adapters", LoadId)), files, baselines)
            .Run("/repo", [], Date, "/repo/src");

        files.Roots.Should().NotBeEmpty().And.OnlyContain(root => root == "/repo/src");
        baselines.SavedPath.Should().Be(Path.Combine("/repo/src", "quality", "baseline.json"));
    }

    private sealed class RootRecordingFiles : IGrandfatherFiles
    {
        public List<string> Roots { get; } = [];

        public IReadOnlyList<string> ExistingProjects(string repoRoot)
        {
            Roots.Add(repoRoot);
            return [];
        }

        public void Write(string repoRoot, string project, string content) => Roots.Add(repoRoot);

        public void Delete(string repoRoot, string project) => Roots.Add(repoRoot);
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Runner_writes_one_file_per_project_with_distinct_members()
    {
        var files = new FakeFiles();
        var scanner = new FakeScanner(
            new BannedCallSite("Sample.Adapters", LoadId),
            new BannedCallSite("Sample.Adapters", LoadId),
            new BannedCallSite("Sample.Other", "M:Sample.Other.X.Y"));

        var result = new GrandfatherRunner(scanner, files, new FakeBaselines(null)).Run("/repo", [], Date);

        files.Written.Keys.Should().BeEquivalentTo("Sample.Adapters", "Sample.Other");
        files.Written["Sample.Adapters"].Split("Target =").Length.Should().Be(2, "duplicate sites collapse to one member entry");
        result.Sites.Should().Be(2);
        result.Projects.Should().Equal("Sample.Adapters", "Sample.Other");
    }

    [Fact]
    [Trait("scenario", "US2-AS46")]
    public void Runner_on_a_clean_project_writes_nothing()
    {
        var files = new FakeFiles();

        var result = new GrandfatherRunner(new FakeScanner(), files, new FakeBaselines(null)).Run("/repo", [], Date);

        files.Written.Should().BeEmpty();
        files.Deleted.Should().BeEmpty();
        result.Sites.Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US2-AS45")]
    public void Runner_removes_stale_files_of_projects_without_sites()
    {
        var files = new FakeFiles("Sample.Gone");

        new GrandfatherRunner(new FakeScanner(), files, new FakeBaselines(null)).Run("/repo", [], Date);

        files.Deleted.Should().Equal("Sample.Gone");
    }

    [Fact]
    [Trait("scenario", "US2-AS45")]
    public void Runner_records_the_grandfathered_count_in_an_existing_baseline()
    {
        var existing = new Baseline("c", "t", [], new Dictionary<string, IReadOnlyDictionary<string, int>>());
        var baselines = new FakeBaselines(existing);

        new GrandfatherRunner(new FakeScanner(new BannedCallSite("Sample.Adapters", LoadId)), new FakeFiles(), baselines)
            .Run("/repo", [], Date);

        baselines.SavedPath.Should().EndWith("baseline.json");
        baselines.Saved!.Grandfathered!["Sample.Adapters"]["RS0030"].Should().Be(1);
    }

    [Fact]
    [Trait("scenario", "US2-AS45")]
    public void Runner_does_not_create_a_baseline_when_none_exists()
    {
        var baselines = new FakeBaselines(null);

        new GrandfatherRunner(new FakeScanner(new BannedCallSite("Sample.Adapters", LoadId)), new FakeFiles(), baselines)
            .Run("/repo", [], Date);

        baselines.Saved.Should().BeNull();
    }

    [Fact]
    [Trait("scenario", "US2-AS45")]
    public void Files_adapter_round_trips_and_counts_grandfathered_sites_per_project()
    {
        var repo = Directory.CreateTempSubdirectory("qg-gf-").FullName;
        try
        {
            var files = new GrandfatherFiles();
            files.Write(repo, "Sample.Adapters", GrandfatherSuppressions.Render([LoadId, "M:Sample.Adapters.Repo.Other"], Date));

            files.ExistingProjects(repo).Should().Equal("Sample.Adapters");
            GrandfatherFiles.CountSites(repo)["Sample.Adapters"]["RS0030"].Should().Be(2);

            files.Delete(repo, "Sample.Adapters");
            files.ExistingProjects(repo).Should().BeEmpty();
            GrandfatherFiles.CountSites(repo).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS59")]
    public void Grandfather_help_exits_zero_and_describes_the_repo_option()
    {
        var (code, output, _) = Run(["grandfather", "--help"]);

        code.Should().Be(0);
        output.Should().Contain("grandfather");
        output.Should().Contain("--repo");
    }

    [Fact]
    [Trait("scenario", "US2-AS59")]
    public void Root_help_lists_grandfather_next_to_the_other_four_modes()
    {
        var (code, output, _) = Run(["--help"]);

        code.Should().Be(0);
        var lines = output.Split('\n').Select(l => l.Trim()).ToList();
        foreach (var mode in new[] { "pr", "mutation-diff", "nightly", "audit", "grandfather" })
        {
            lines.Should().Contain(l => l == mode || l.StartsWith(mode + " ", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS59")]
    public void Grandfather_without_repo_is_a_parse_error_with_exit_three()
    {
        var (code, _, err) = Run(["grandfather"]);

        code.Should().Be(3);
        err.Should().Contain("--repo");
    }

    [Fact]
    [Trait("scenario", "US2-AS59")]
    public void Grandfather_dispatches_to_the_handler_override()
    {
        string? seen = null;

        var (code, _, _) = Run(["grandfather", "--repo", "."], mode =>
        {
            seen = mode;
            return 0;
        });

        code.Should().Be(0);
        seen.Should().Be("grandfather");
    }

    private static (int Code, string Out, string Err) Run(string[] args, Func<string, int>? handler = null)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = CliHost.Run(args, o, e, handler);
        return (code, o.ToString(), e.ToString());
    }
}
