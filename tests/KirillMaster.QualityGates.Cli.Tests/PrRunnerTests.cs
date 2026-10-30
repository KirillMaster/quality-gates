using FluentAssertions;
using KirillMaster.QualityGates.Cli.Application;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Application.UseCases;
using KirillMaster.QualityGates.Cli.Domain.Coverage;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class PrRunnerTests
{
    private const string Repo = "/repo";
    private static readonly string BaselinePath = Path.Combine(Repo, "quality", "baseline.json");
    private static readonly string ConfigPath = Path.Combine(Repo, "quality", "gates.json");
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Fakes : IConfigStore, IBaselineStore, IGitClient, ICoverageReader, ISarifSource, IProjectLocator, IPrFiles, IReportStore
    {
        public ConfigLoadResult ConfigResult = ConfigLoadResult.Ok(GatesConfig.Default);
        public bool ConfigExists;
        public string? LoadedConfigPath;
        public Baseline? Previous;
        public string? LoadedBaselinePath;
        public List<(string Path, Baseline Baseline)> Saved = [];
        public string Diff = string.Empty;
        public string Head = "abc123";
        public List<string> DiffTargets = [];
        public CancellationToken? DiffToken;
        public CancellationToken? HeadToken;
        public CoverageReport Coverage = new();
        public int ReadMergedCalls;
        public IReadOnlyList<string>? ReadFiles;
        public string? ReadRoot;
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> SarifResult = new Dictionary<string, IReadOnlyDictionary<string, int>>();
        public IReadOnlyList<string>? SarifFiles;
        public IReadOnlyList<Finding> Findings = [];
        public List<(string Out, DebtReport Report)> Debts = [];
        public Dictionary<string, ProjectRef?> Projects = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Repo, string File, IReadOnlyDictionary<string, Layer> Overrides)> Locates = [];
        public Dictionary<string, string[]> Globs = new() { ["cov"] = ["c.xml"], ["sarif"] = ["s.sarif"] };
        public List<string> Expanded = [];
        public List<(string Out, RunReport Report)> Written = [];
        public List<string> Calls = [];

        public ConfigLoadResult Load(string path)
        {
            LoadedConfigPath = path;
            return ConfigResult;
        }

        Baseline? IBaselineStore.Load(string path)
        {
            LoadedBaselinePath = path;
            return Previous;
        }

        public void Save(string path, Baseline baseline) => Saved.Add((path, baseline));

        public Task<string> DiffAsync(string repoPath, string targetBranch, CancellationToken cancellationToken = default)
        {
            Calls.Add("diff");
            DiffTargets.Add(repoPath + "|" + targetBranch);
            DiffToken = cancellationToken;
            return Task.FromResult(Diff);
        }

        public Task<string> HeadAsync(string repoPath, CancellationToken cancellationToken = default)
        {
            Calls.Add("head");
            HeadToken = cancellationToken;
            return Task.FromResult(Head);
        }

        public CoverageReport ReadMerged(IReadOnlyList<string> files, string repoRoot)
        {
            ReadMergedCalls++;
            ReadFiles = files;
            ReadRoot = repoRoot;
            return Coverage;
        }

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Aggregate(IReadOnlyList<string> files)
        {
            Calls.Add("sarif");
            SarifFiles = files;
            return SarifResult;
        }

        public IReadOnlyList<Finding> Read(IReadOnlyList<string> files) => Findings;

        public ProjectRef? Locate(string repoRoot, string relativeFile, IReadOnlyDictionary<string, Layer> overrides)
        {
            Locates.Add((repoRoot, relativeFile, overrides));
            return Projects.GetValueOrDefault(relativeFile);
        }

        public IReadOnlyList<string> Expand(IEnumerable<string> patterns)
        {
            var list = patterns.ToList();
            Expanded.AddRange(list);
            return list.SelectMany(p => Globs.GetValueOrDefault(p) ?? []).ToList();
        }

        public bool Exists(string path) => ConfigExists && path == ConfigPath;

        public void Write(string outDirectory, RunReport report) => Written.Add((outDirectory, report));

        public void WriteDebt(string outDirectory, DebtReport report) => Debts.Add((outDirectory, report));

        public PrRunner Runner() => new(this, this, this, this, this, this, this, this, new Fixed(Now));
    }

    private static PrRequest Request(
        IReadOnlyCollection<QualityGroup>? skip = null,
        bool? integration = null,
        string? bypassed = null) =>
        new(Repo, "origin/develop", "feature/x", ["cov"], ["sarif"], "/out", integration, skip, bypassed);

    private static Baseline Prev(double coreCoverage = 100) =>
        new("old", "t", [new ProjectMetrics("Sample.Domain", Layer.Core, coreCoverage)], new Dictionary<string, IReadOnlyDictionary<string, int>>());

    private static Fakes WithDomainFile(params (int Line, int Hits)[] lines)
    {
        var fakes = new Fakes();
        foreach (var (line, hits) in lines)
        {
            fakes.Coverage.Record("src/Domain/Calc.cs", line, hits);
        }

        fakes.Projects["src/Domain/Calc.cs"] = new ProjectRef("Sample.Domain", Layer.Core);
        return fakes;
    }

    private static GateResult Gate(PrOutcome outcome, GateId gate, ResultLayer? layer = null) =>
        outcome.Report.Results.Single(r => r.Gate == gate && r.Layer == layer);

    private const string DomainDiff = "diff --git a/src/Domain/Calc.cs b/src/Domain/Calc.cs\n--- a/src/Domain/Calc.cs\n+++ b/src/Domain/Calc.cs\n@@ -7,0 +8 @@\n+x\n";
    private const string AdapterDiff = "diff --git a/src/Adapters/Repo.cs b/src/Adapters/Repo.cs\n--- a/src/Adapters/Repo.cs\n+++ b/src/Adapters/Repo.cs\n@@ -7,0 +8 @@\n+x\n";

    private static Fakes WeakAdapter(GatesConfig? config = null)
    {
        var f = new Fakes();
        if (config is not null)
        {
            f.ConfigExists = true;
            f.ConfigResult = ConfigLoadResult.Ok(config);
        }

        f.Coverage.Record("src/Adapters/Repo.cs", 8, 0);
        f.Projects["src/Adapters/Repo.cs"] = new ProjectRef("Sample.Adapters", Layer.Adapter);
        f.Previous = new Baseline("old", "t", [], new Dictionary<string, IReadOnlyDictionary<string, int>>());
        f.Diff = AdapterDiff;
        return f;
    }

    [Fact]
    [Trait("scenario", "US2-AS48")]
    public async Task Clean_run_exits_zero_with_all_gates_passing_and_report_written()
    {
        var f = WithDomainFile((8, 1), (9, 1));
        f.Previous = Prev(100);
        f.Diff = DomainDiff;

        var outcome = await f.Runner().RunAsync(Request());

        outcome.ExitCode.Should().Be(0);
        outcome.Report.ExitCode.Should().Be(0);
        outcome.Report.Mode.Should().Be(RunMode.Pr);
        outcome.Report.Policy.Should().Be(Policy.Blocking);
        outcome.Report.SourceBranch.Should().Be("feature/x");
        outcome.Report.TargetBranch.Should().Be("origin/develop");
        outcome.Report.Error.Should().BeNull();
        outcome.Report.BypassedBy.Should().BeNull();
        outcome.Report.SkippedGroups.Should().BeEmpty();
        Gate(outcome, GateId.StaticRatchet).Verdict.Should().Be(Verdict.Pass);
        Gate(outcome, GateId.DiffCoverage, ResultLayer.Core).Verdict.Should().Be(Verdict.Pass);
        Gate(outcome, GateId.DiffCoverage, ResultLayer.Adapter).Verdict.Should().Be(Verdict.NotApplicable);
        Gate(outcome, GateId.CoverageRatchet).Verdict.Should().Be(Verdict.Pass);
        outcome.Report.Results.Should().HaveCount(5);
        f.Written.Should().ContainSingle().Which.Out.Should().Be("/out");
        f.Written[0].Report.Should().BeSameAs(outcome.Report);
        f.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task Structural_change_keeps_diff_coverage_on_added_lines_and_exempts_only_moved_ones()
    {
        var structural = new PrProcessOptions(ChangeKind.Structural, BaseCoverage: ["base.xml"], BaseCoverageCommit: "abc");
        var added = WithDomainFile((8, 0));
        added.Previous = Prev(0);
        added.Diff = DomainDiff;
        var moved = WithDomainFile((8, 0));
        moved.Previous = Prev(0);
        moved.Diff = "diff --git a/src/Domain/Old.cs b/src/Domain/Old.cs\n--- a/src/Domain/Old.cs\n+++ b/src/Domain/Old.cs\n@@ -3 +2,0 @@\n-return value;\n"
            + "diff --git a/src/Domain/Calc.cs b/src/Domain/Calc.cs\n--- a/src/Domain/Calc.cs\n+++ b/src/Domain/Calc.cs\n@@ -7,0 +8 @@\n+return value;\n";

        var addedOutcome = await added.Runner().RunAsync(Request() with { Process = structural });
        var movedOutcome = await moved.Runner().RunAsync(Request() with { Process = structural });

        Gate(addedOutcome, GateId.DiffCoverage, ResultLayer.Core).Verdict.Should().Be(Verdict.Fail);
        Gate(addedOutcome, GateId.DiffCoverage, ResultLayer.Core).Offenders.Should().Equal("src/Domain/Calc.cs:8");
        Gate(movedOutcome, GateId.DiffCoverage, ResultLayer.Core).Verdict.Should().NotBe(Verdict.Fail);
    }

    [Fact]
    [Trait("scenario", "US2-AS47")]
    public async Task Uncovered_changed_core_line_exits_one_with_offender()
    {
        var f = WithDomainFile((8, 0));
        f.Previous = Prev(0);
        f.Diff = DomainDiff;

        var outcome = await f.Runner().RunAsync(Request());

        outcome.ExitCode.Should().Be(1);
        var core = Gate(outcome, GateId.DiffCoverage, ResultLayer.Core);
        core.Verdict.Should().Be(Verdict.Fail);
        core.Offenders.Should().Equal("src/Domain/Calc.cs:8");
        f.DiffTargets.Should().Equal(Repo + "|origin/develop");
    }

    [Fact]
    [Trait("scenario", "US2-AS49")]
    public async Task Weak_adapter_warns_exit_two_when_integration_disabled_by_request_and_fails_otherwise()
    {
        (await WeakAdapter().Runner().RunAsync(Request())).ExitCode.Should().Be(1);
        var warned = await WeakAdapter().Runner().RunAsync(Request(integration: false));
        warned.ExitCode.Should().Be(2);
        Gate(warned, GateId.DiffCoverage, ResultLayer.Adapter).Verdict.Should().Be(Verdict.Warn);
    }

    [Fact]
    [Trait("scenario", "US2-AS56")]
    public async Task Request_integration_flag_overrides_config_and_null_keeps_config_value()
    {
        var off = GatesConfig.Default with { IntegrationTestsEnabled = false };
        var on = GatesConfig.Default with { IntegrationTestsEnabled = true };

        (await WeakAdapter(off).Runner().RunAsync(Request())).ExitCode.Should().Be(2);
        (await WeakAdapter(on).Runner().RunAsync(Request())).ExitCode.Should().Be(1);
        (await WeakAdapter(off).Runner().RunAsync(Request(integration: true))).ExitCode.Should().Be(1);
        (await WeakAdapter(on).Runner().RunAsync(Request(integration: false))).ExitCode.Should().Be(2);
    }

    [Fact]
    [Trait("scenario", "US2-AS50")]
    public async Task Missing_baseline_exits_two_and_saves_initial_baseline_to_out_dir()
    {
        var f = WithDomainFile((8, 1), (9, 0), (10, 1));
        f.Head = "deadbeef";
        f.Previous = null;

        var outcome = await f.Runner().RunAsync(Request());

        outcome.ExitCode.Should().Be(2);
        f.LoadedBaselinePath.Should().Be(BaselinePath);
        var saved = f.Saved.Should().ContainSingle().Subject;
        saved.Path.Should().Be(Path.Combine("/out", "baseline.json"));
        saved.Baseline.Commit.Should().Be("deadbeef");
        saved.Baseline.MeasuredAt.Should().Be(Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        saved.Baseline.Projects.Should().Equal(new ProjectMetrics("Sample.Domain", Layer.Core, 66.67));
        Gate(outcome, GateId.StaticRatchet).Verdict.Should().Be(Verdict.Warn);
        Gate(outcome, GateId.CoverageRatchet).Verdict.Should().Be(Verdict.Warn);
    }

    [Fact]
    [Trait("scenario", "US2-AS50")]
    public async Task Baseline_measurement_aggregates_per_project_skips_test_excluded_and_non_cs_and_sorts_by_name()
    {
        var f = new Fakes { Previous = null };
        void Add(string file, ProjectRef? project, params (int, int)[] lines)
        {
            foreach (var (l, h) in lines)
            {
                f.Coverage.Record(file, l, h);
            }

            f.Coverage.AddFile(file);
            f.Projects[file] = project;
        }

        Add("src/B/One.cs", new ProjectRef("B.Adapters", Layer.Adapter), (1, 1), (2, 0));
        Add("src/B/Two.cs", new ProjectRef("B.Adapters", Layer.Adapter), (1, 1));
        Add("src/A/One.cs", new ProjectRef("A.Domain", Layer.Core), (1, 1));
        Add("tests/T.cs", new ProjectRef("A.Tests", Layer.Test), (1, 1));
        Add("src/X.cs", new ProjectRef("X.Excluded", Layer.Excluded), (1, 1));
        Add("src/Orphan.cs", null, (1, 1));
        Add("src/Empty.cs", new ProjectRef("Empty.Domain", Layer.Core));
        Add("src/Zero.cs", new ProjectRef("Zero.Domain", Layer.Core), (1, 0));
        f.Coverage.Record("src/readme.md", 1, 1);

        await f.Runner().RunAsync(Request());

        f.Locates.Select(l => l.File).Should().NotContain("src/readme.md");
        f.Saved.Single().Baseline.Projects.Should().Equal(
            new ProjectMetrics("A.Domain", Layer.Core, 100),
            new ProjectMetrics("B.Adapters", Layer.Adapter, 66.67),
            new ProjectMetrics("Zero.Domain", Layer.Core, 0));
    }

    [Fact]
    [Trait("scenario", "US2-AS55")]
    public async Task Coverage_drop_beyond_tolerance_fails_ratchet()
    {
        var f = WithDomainFile((8, 1), (9, 0));
        f.Previous = Prev(90);
        f.Diff = DomainDiff;

        var outcome = await f.Runner().RunAsync(Request());

        outcome.ExitCode.Should().Be(1);
        Gate(outcome, GateId.CoverageRatchet).Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    [Trait("scenario", "US2-AS55")]
    public async Task Sarif_warning_increase_fails_static_ratchet_and_inputs_are_forwarded()
    {
        var f = WithDomainFile((8, 1));
        f.Previous = Prev(100);
        f.Diff = DomainDiff;
        f.SarifResult = new Dictionary<string, IReadOnlyDictionary<string, int>> { ["Sample.Domain"] = new Dictionary<string, int> { ["CA1822"] = 1 } };

        var outcome = await f.Runner().RunAsync(Request());

        outcome.ExitCode.Should().Be(1);
        Gate(outcome, GateId.StaticRatchet).Verdict.Should().Be(Verdict.Fail);
        f.SarifFiles.Should().Equal("s.sarif");
        f.ReadFiles.Should().Equal("c.xml");
        f.ReadRoot.Should().Be(Repo);
    }

    [Fact]
    [Trait("scenario", "US2-AS53")]
    public async Task Docs_only_change_is_not_applicable_and_exits_zero()
    {
        var f = WithDomainFile((8, 1));
        f.Previous = Prev(100);
        f.Diff = "diff --git a/README.md b/README.md\n--- a/README.md\n+++ b/README.md\n@@ -1,0 +2 @@\n+x\n";

        var outcome = await f.Runner().RunAsync(Request());

        outcome.ExitCode.Should().Be(0);
        Gate(outcome, GateId.DiffCoverage, ResultLayer.Core).Verdict.Should().Be(Verdict.NotApplicable);
        Gate(outcome, GateId.DiffCoverage, ResultLayer.Adapter).Verdict.Should().Be(Verdict.NotApplicable);
        f.Locates.Select(l => l.File).Should().NotContain("README.md");
    }

    [Fact]
    [Trait("scenario", "US2-AS51")]
    public async Task Invalid_config_exits_three_naming_the_field_without_touching_other_ports()
    {
        var f = new Fakes { ConfigExists = true, ConfigResult = ConfigLoadResult.Fail("diffCoverage.core", "must be a number") };

        var outcome = await f.Runner().RunAsync(Request(skip: [QualityGroup.Mutation]));

        outcome.ExitCode.Should().Be(3);
        outcome.Report.Error.Should().Be("invalid gates.json, field diffCoverage.core: must be a number");
        outcome.Report.Results.Should().BeEmpty();
        outcome.Report.Mode.Should().Be(RunMode.Pr);
        outcome.Report.Policy.Should().Be(Policy.Blocking);
        outcome.Report.SourceBranch.Should().Be("feature/x");
        outcome.Report.TargetBranch.Should().Be("origin/develop");
        outcome.Report.SkippedGroups.Should().Equal("mutation");
        f.LoadedConfigPath.Should().Be(ConfigPath);
        f.Expanded.Should().BeEmpty();
        f.Calls.Should().BeEmpty();
        f.Saved.Should().BeEmpty();
        f.Written.Should().ContainSingle().Which.Report.ExitCode.Should().Be(3);
    }

    [Fact]
    [Trait("scenario", "US2-AS51")]
    public async Task Config_is_not_loaded_when_gates_json_is_absent()
    {
        var f = WithDomainFile((8, 1));
        f.Previous = Prev(100);

        await f.Runner().RunAsync(Request());

        f.LoadedConfigPath.Should().BeNull();
    }

    [Fact]
    [Trait("scenario", "US2-AS52")]
    public async Task Missing_coverage_files_exit_three_listing_the_globs()
    {
        var f = new Fakes();
        f.Globs.Remove("cov");

        var outcome = await f.Runner().RunAsync(new PrRequest(Repo, "t", "s", ["a/*.xml", "b/*.xml"], ["sarif"], "/out"));

        outcome.ExitCode.Should().Be(3);
        outcome.Report.Error.Should().Be("no coverage files were found for: a/*.xml, b/*.xml");
        f.Calls.Should().BeEmpty();
    }

    [Fact]
    [Trait("scenario", "US2-AS52")]
    public async Task Missing_sarif_files_exit_three_listing_the_globs()
    {
        var f = new Fakes();
        f.Globs.Remove("sarif");

        var outcome = await f.Runner().RunAsync(new PrRequest(Repo, "t", "s", ["cov"], ["x/*.sarif", "y/*.sarif"], "/out"));

        outcome.ExitCode.Should().Be(3);
        outcome.Report.Error.Should().Be("no SARIF files were found for: x/*.sarif, y/*.sarif");
        f.ReadMergedCalls.Should().Be(0);
    }

    [Fact]
    [Trait("scenario", "US2-AS52")]
    public async Task Skipped_groups_do_not_require_their_input_files()
    {
        var f = new Fakes();
        f.Globs.Clear();

        var outcome = await f.Runner().RunAsync(Request(skip: [QualityGroup.Static, QualityGroup.Coverage]));

        outcome.ExitCode.Should().Be(0);
        outcome.Report.Error.Should().BeNull();
    }

    [Fact]
    [Trait("scenario", "US2-AS54")]
    public async Task Skip_all_bypasses_every_gate_exits_zero_and_touches_no_inputs()
    {
        var f = new Fakes();
        f.Globs.Clear();

        var outcome = await f.Runner().RunAsync(Request(skip: [QualityGroup.Static, QualityGroup.Coverage, QualityGroup.Mutation], bypassed: "alice"));

        outcome.ExitCode.Should().Be(0);
        outcome.Report.BypassedBy.Should().Be("alice");
        outcome.Report.SkippedGroups.Should().Equal("coverage", "mutation", "static");
        outcome.Report.Results.Should().HaveCount(6);
        outcome.Report.Results.Should().OnlyContain(r => r.Verdict == Verdict.Skip && r.Message == PrRunner.BypassedMessage);
        outcome.Report.Results.Select(r => (r.Gate, r.Layer)).Should().Equal(
            (GateId.StaticRatchet, (ResultLayer?)null),
            (GateId.NewCodeViolations, null),
            (GateId.DiffCoverage, ResultLayer.Core),
            (GateId.DiffCoverage, ResultLayer.Adapter),
            (GateId.CoverageRatchet, null),
            (GateId.MutationDiff, null));
        f.Expanded.Should().BeEmpty();
        f.Calls.Should().BeEmpty();
        f.ReadMergedCalls.Should().Be(0);
        f.LoadedBaselinePath.Should().BeNull();
        f.Saved.Should().BeEmpty();
    }

    [Fact]
    [Trait("scenario", "US2-AS54")]
    public async Task Skip_mutation_only_still_evaluates_static_and_coverage()
    {
        var f = WithDomainFile((8, 1));
        f.Previous = Prev(100);
        f.Diff = DomainDiff;

        var outcome = await f.Runner().RunAsync(Request(skip: [QualityGroup.Mutation]));

        outcome.ExitCode.Should().Be(0);
        outcome.Report.Results.Should().HaveCount(6);
        Gate(outcome, GateId.MutationDiff).Verdict.Should().Be(Verdict.Skip);
        Gate(outcome, GateId.StaticRatchet).Verdict.Should().Be(Verdict.Pass);
        Gate(outcome, GateId.DiffCoverage, ResultLayer.Core).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    [Trait("scenario", "US2-AS54")]
    public async Task Skip_static_bypasses_only_static_and_ignores_sarif()
    {
        var f = WithDomainFile((8, 1));
        f.Previous = Prev(100);
        f.Diff = DomainDiff;
        f.SarifResult = new Dictionary<string, IReadOnlyDictionary<string, int>> { ["Sample.Domain"] = new Dictionary<string, int> { ["CA1"] = 5 } };

        var outcome = await f.Runner().RunAsync(Request(skip: [QualityGroup.Static]));

        outcome.ExitCode.Should().Be(0);
        outcome.Report.Results.Should().HaveCount(5);
        Gate(outcome, GateId.StaticRatchet).Should().Be(new GateResult(GateId.StaticRatchet, Verdict.Skip, null, Message: PrRunner.BypassedMessage));
        Gate(outcome, GateId.CoverageRatchet).Verdict.Should().Be(Verdict.Pass);
        f.Calls.Should().NotContain("sarif");
        f.Expanded.Should().Equal("cov");
    }

    [Fact]
    [Trait("scenario", "US2-AS54")]
    public async Task Skip_static_with_missing_baseline_saves_baseline_with_empty_warnings()
    {
        var f = WithDomainFile((8, 1));
        f.Previous = null;
        f.SarifResult = new Dictionary<string, IReadOnlyDictionary<string, int>> { ["Sample.Domain"] = new Dictionary<string, int> { ["CA1"] = 5 } };

        await f.Runner().RunAsync(Request(skip: [QualityGroup.Static]));

        f.Saved.Single().Baseline.Warnings.Should().BeEmpty();
        f.Saved.Single().Baseline.Projects.Should().HaveCount(1);
    }

    [Fact]
    [Trait("scenario", "US2-AS54")]
    public async Task Skip_coverage_bypasses_coverage_gates_but_still_runs_static_ratchet()
    {
        var f = new Fakes { Previous = Prev(100) };
        f.SarifResult = new Dictionary<string, IReadOnlyDictionary<string, int>> { ["Sample.Domain"] = new Dictionary<string, int> { ["CA1"] = 1 } };

        var outcome = await f.Runner().RunAsync(Request(skip: [QualityGroup.Coverage]));

        outcome.ExitCode.Should().Be(1);
        outcome.Report.Results.Should().HaveCount(5);
        Gate(outcome, GateId.StaticRatchet).Verdict.Should().Be(Verdict.Fail);
        Gate(outcome, GateId.DiffCoverage, ResultLayer.Core).Verdict.Should().Be(Verdict.Skip);
        Gate(outcome, GateId.DiffCoverage, ResultLayer.Adapter).Verdict.Should().Be(Verdict.Skip);
        Gate(outcome, GateId.CoverageRatchet).Verdict.Should().Be(Verdict.Skip);
        f.ReadMergedCalls.Should().Be(0);
        f.Calls.Should().NotContain("diff");
        f.Expanded.Should().Equal("sarif");
    }

    [Fact]
    [Trait("scenario", "US2-AS50")]
    public async Task Skip_coverage_with_missing_baseline_saves_baseline_without_projects()
    {
        var f = new Fakes { Previous = null };

        var outcome = await f.Runner().RunAsync(Request(skip: [QualityGroup.Coverage]));

        outcome.ExitCode.Should().Be(2);
        f.Saved.Single().Baseline.Projects.Should().BeEmpty();
    }

    [Fact]
    [Trait("scenario", "US2-AS48")]
    public async Task Layer_overrides_reach_locator_each_file_is_located_once_and_token_is_forwarded()
    {
        var overrides = new Dictionary<string, Layer> { ["Sample.Domain"] = Layer.Adapter };
        var f = WithDomainFile((8, 1));
        f.ConfigExists = true;
        f.ConfigResult = ConfigLoadResult.Ok(GatesConfig.Default with { LayerOverrides = overrides });
        f.Previous = Prev(100);
        f.Diff = DomainDiff;
        using var cts = new CancellationTokenSource();

        await f.Runner().RunAsync(Request(), cts.Token);

        f.Locates.Should().ContainSingle();
        f.Locates[0].Repo.Should().Be(Repo);
        f.Locates[0].File.Should().Be("src/Domain/Calc.cs");
        f.Locates[0].Overrides.Should().BeSameAs(overrides);
        f.DiffToken.Should().Be(cts.Token);
        f.HeadToken.Should().Be(cts.Token);
    }

    [Fact]
    [Trait("scenario", "US2-AS48")]
    public async Task Unlocatable_cs_file_is_excluded_from_diff_coverage()
    {
        var f = new Fakes { Previous = Prev(100) };
        f.Coverage.Record("src/Orphan.cs", 8, 0);
        f.Diff = "diff --git a/src/Orphan.cs b/src/Orphan.cs\n--- a/src/Orphan.cs\n+++ b/src/Orphan.cs\n@@ -7,0 +8 @@\n+x\n";

        var outcome = await f.Runner().RunAsync(Request());

        outcome.ExitCode.Should().Be(0);
        Gate(outcome, GateId.DiffCoverage, ResultLayer.Core).Verdict.Should().Be(Verdict.NotApplicable);
        Gate(outcome, GateId.DiffCoverage, ResultLayer.Adapter).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    [Trait("scenario", "US2-AS48")]
    public async Task Cs_extension_match_is_case_insensitive()
    {
        var f = new Fakes { Previous = Prev(100) };
        f.Coverage.Record("src/Domain/Calc.CS", 8, 0);
        f.Projects["src/Domain/Calc.CS"] = new ProjectRef("Sample.Domain", Layer.Core);
        f.Diff = "diff --git a/src/Domain/Calc.CS b/src/Domain/Calc.CS\n--- a/src/Domain/Calc.CS\n+++ b/src/Domain/Calc.CS\n@@ -7,0 +8 @@\n+x\n";

        var outcome = await f.Runner().RunAsync(Request());

        Gate(outcome, GateId.DiffCoverage, ResultLayer.Core).Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    [Trait("scenario", "RES-14256")]
    public async Task Warning_on_changed_line_fails_new_code_gate_even_when_counts_equal_baseline()
    {
        var f = WithDomainFile((8, 1));
        f.Previous = Prev(100) with { Warnings = new Dictionary<string, IReadOnlyDictionary<string, int>> { ["Sample.Domain"] = new Dictionary<string, int> { ["CA1"] = 1 } } };
        f.SarifResult = new Dictionary<string, IReadOnlyDictionary<string, int>> { ["Sample.Domain"] = new Dictionary<string, int> { ["CA1"] = 1 } };
        f.Findings = [new Finding("Sample.Domain", "CA1", "file:///repo/src/Domain/Calc.cs", 8)];
        f.Diff = DomainDiff;

        var outcome = await f.Runner().RunAsync(Request());

        outcome.ExitCode.Should().Be(1);
        Gate(outcome, GateId.StaticRatchet).Verdict.Should().Be(Verdict.Pass);
        Gate(outcome, GateId.NewCodeViolations).Offenders.Should().Equal("src/Domain/Calc.cs:8 CA1");
        f.DiffTargets.Should().ContainSingle();
    }

    [Fact]
    [Trait("scenario", "RES-14256")]
    public async Task Warning_on_unchanged_line_passes_new_code_gate()
    {
        var f = WithDomainFile((8, 1));
        f.Previous = Prev(100);
        f.Findings = [new Finding("Sample.Domain", "CA1", "file:///repo/src/Domain/Calc.cs", 3)];
        f.Diff = DomainDiff;

        var outcome = await f.Runner().RunAsync(Request());

        outcome.ExitCode.Should().Be(0);
        Gate(outcome, GateId.NewCodeViolations).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    [Trait("scenario", "RES-14256")]
    public async Task Skip_static_bypasses_new_code_gate()
    {
        var f = WithDomainFile((8, 1));
        f.Previous = Prev(100);
        f.Findings = [new Finding("Sample.Domain", "CA1", "file:///repo/src/Domain/Calc.cs", 8)];
        f.Diff = DomainDiff;

        var outcome = await f.Runner().RunAsync(Request(skip: [QualityGroup.Static]));

        outcome.ExitCode.Should().Be(0);
        Gate(outcome, GateId.NewCodeViolations).Verdict.Should().Be(Verdict.Skip);
        f.Debts.Should().BeEmpty();
    }

    [Fact]
    [Trait("scenario", "RES-14256")]
    public async Task Lower_counts_write_tightened_baseline_and_do_not_fail()
    {
        var f = WithDomainFile((8, 1));
        f.Previous = Prev(100) with { Warnings = new Dictionary<string, IReadOnlyDictionary<string, int>> { ["Sample.Domain"] = new Dictionary<string, int> { ["CA1"] = 3, ["CA2"] = 1 } } };
        f.SarifResult = new Dictionary<string, IReadOnlyDictionary<string, int>> { ["Sample.Domain"] = new Dictionary<string, int> { ["CA1"] = 2 } };
        f.Diff = DomainDiff;

        var outcome = await f.Runner().RunAsync(Request());

        outcome.ExitCode.Should().Be(0);
        Gate(outcome, GateId.StaticRatchet).Message.Should().Contain("tightened");
        var saved = f.Saved.Should().ContainSingle().Subject;
        saved.Path.Should().Be(Path.Combine("/out", "baseline.json"));
        saved.Baseline.Warnings["Sample.Domain"].Should().Equal(new Dictionary<string, int> { ["CA1"] = 2 });
    }

    [Fact]
    [Trait("scenario", "RES-14256")]
    public async Task Debt_report_is_written_to_out_with_findings_and_baseline_counts()
    {
        var f = WithDomainFile((8, 1));
        f.Previous = Prev(100);
        f.Findings = [new Finding("Sample.Domain", "CA1", "file:///repo/src/Domain/Calc.cs", 3)];
        f.Diff = DomainDiff;

        await f.Runner().RunAsync(Request());

        var debt = f.Debts.Should().ContainSingle().Subject;
        debt.Out.Should().Be("/out");
        debt.Report.Findings.Should().Equal(f.Findings);
    }
}
