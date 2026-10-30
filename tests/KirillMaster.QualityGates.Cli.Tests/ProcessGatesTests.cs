using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Roslyn;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Application.UseCases;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class ProcessGatesTests
{
    private static readonly RoslynCodeInsights Insights = new();

    private static IReadOnlyDictionary<string, int> Counts(params (string Key, int Count)[] entries) => entries.ToDictionary(e => e.Key, e => e.Count);

    [Fact]
    public void Cognitive_complexity_counts_nesting_else_if_and_logical_sequences()
    {
        const string Source = """
            class C {
                int M(int a, int b) {
                    if (a > 0 && b > 0 || a > 5) {
                        for (var i = 0; i < a; i++) { if (i == b) { return i; } }
                    } else if (b > 1) { return 1; } else { return 2; }
                    return a > b ? a : b;
                }
                int Simple() => 1;
            }
            """;

        var counts = Insights.CognitiveComplexity(Source);

        counts["C.Simple()"].Should().Be(0);
        counts["C.M(int,int)"].Should().Be(1 + 2 + 2 + 3 + 1 + 1 + 1);
    }

    [Fact]
    public void Cognitive_complexity_delta_fails_on_growth_and_on_a_complex_new_method_only()
    {
        var grown = new MethodComplexityChange("A.cs", Counts(("A.M()", 2)), Counts(("A.M()", 3)));
        var simpler = new MethodComplexityChange("B.cs", Counts(("B.M()", 5)), Counts(("B.M()", 4), ("B.N()", 15)));
        var heavyNew = new MethodComplexityChange("C.cs", Counts(), Counts(("C.M()", 16)));

        CognitiveComplexityDeltaGate.Evaluate([simpler], new ComplexityDeltaOptions()).Verdict.Should().Be(Verdict.Pass);
        CognitiveComplexityDeltaGate.Evaluate([grown], new ComplexityDeltaOptions()).Offenders.Should().Equal("A.cs: A.M() 2 -> 3");
        CognitiveComplexityDeltaGate.Evaluate([heavyNew], new ComplexityDeltaOptions()).Verdict.Should().Be(Verdict.Fail);
        CognitiveComplexityDeltaGate.Evaluate([], new ComplexityDeltaOptions()).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Toggle_expiry_warns_on_expired_and_malformed_dates_only()
    {
        var today = new DateOnly(2026, 10, 30);

        ToggleExpiryGate.Evaluate([new ToggleMarker("A.cs", 3, "// qg-toggle-expiry: 2026-12-01")], today).Verdict.Should().Be(Verdict.Pass);
        ToggleExpiryGate.Evaluate([new ToggleMarker("A.cs", 3, "// qg-toggle-expiry: 2026-10-29")], today).Offenders.Should().Equal("A.cs:3: toggle expired on 2026-10-29");
        ToggleExpiryGate.Evaluate([new ToggleMarker("A.cs", 4, "// qg-toggle-expiry: soon")], today).Verdict.Should().Be(Verdict.Warn);
        ToggleExpiryGate.Evaluate([], today).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Dead_public_reports_types_referenced_only_by_their_own_file()
    {
        var identifiers = new Dictionary<string, IReadOnlySet<string>>
        {
            ["A.cs"] = new HashSet<string> { "Used", "Dead" },
            ["B.cs"] = new HashSet<string> { "Used" }
        };

        var result = DeadPublicGate.Evaluate([new PublicType("A.cs", "Used"), new PublicType("A.cs", "Dead")], identifiers);

        result.Verdict.Should().Be(Verdict.Warn);
        result.Offenders.Should().Equal("A.cs: public Dead is not referenced from any other file");
        DeadPublicGate.Evaluate([], identifiers).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Transitive_fan_out_grandfathers_recorded_projects_up_to_their_baseline_value()
    {
        var graph = ProjectGraph.Build(new Dictionary<string, string>
        {
            ["src/A/A.csproj"] = "<Project><ItemGroup><ProjectReference Include=\"..\\B\\B.csproj\" /></ItemGroup></Project>",
            ["src/B/B.csproj"] = "<Project><ItemGroup><ProjectReference Include=\"..\\C\\C.csproj\" /></ItemGroup></Project>",
            ["src/C/C.csproj"] = "<Project />"
        });

        TransitiveFanOutGate.Evaluate(graph, ["A"], 1, new Dictionary<string, int> { ["A"] = 2 }).Verdict.Should().Be(Verdict.Pass);
        var grown = TransitiveFanOutGate.Evaluate(graph, ["A"], 1, new Dictionary<string, int> { ["A"] = 1 });
        grown.Verdict.Should().Be(Verdict.Warn);
        grown.Offenders.Should().ContainSingle().Which.Should().Contain("over baseline");
        TransitiveFanOutGate.Evaluate(graph, ["A"], 1, new Dictionary<string, int> { ["Other"] = 9 }).Verdict.Should().Be(Verdict.Warn);
    }

    [Fact]
    public void Baseline_counts_include_the_fan_out_section_so_an_increase_is_growth()
    {
        const string before = "{\"warnings\":{},\"fanOut\":{\"Xpm.ArchitectureTests\":35}}";
        const string after = "{\"warnings\":{},\"fanOut\":{\"Xpm.ArchitectureTests\":36}}";
        var delta = new BaselineDelta("quality/baseline.json", BaselineCounts.Parse("quality/baseline.json", before), BaselineCounts.Parse("quality/baseline.json", after));

        var result = BaselineGrowthGate.Evaluate([delta], [], new HashSet<string>());

        result.Verdict.Should().Be(Verdict.Fail);
        result.Offenders.Should().ContainSingle().Which.Should().Contain("fanOut/Xpm.ArchitectureTests 35 -> 36");
    }

    [Fact]
    public void Fan_out_baseline_runner_records_adds_lowers_and_never_raises()
    {
        var store = new FanOutStore(new Baseline("c", "t", [], new Dictionary<string, IReadOnlyDictionary<string, int>>(), FanOut: new Dictionary<string, int> { ["A"] = 5, ["B"] = 1, ["Gone"] = 3 }));
        var projects = new Dictionary<string, string>
        {
            ["/r/A/A.csproj"] = "<Project><ItemGroup><ProjectReference Include=\"..\\B\\B.csproj\" /><ProjectReference Include=\"..\\C\\C.csproj\" /></ItemGroup></Project>",
            ["/r/B/B.csproj"] = "<Project><ItemGroup><ProjectReference Include=\"..\\C\\C.csproj\" /></ItemGroup></Project>",
            ["/r/C/C.csproj"] = "<Project />",
            ["/r/D/D.csproj"] = "<Project><ItemGroup><ProjectReference Include=\"..\\A\\A.csproj\" /></ItemGroup></Project>"
        };

        var result = new FanOutBaselineRunner(store, new FixedFiles([.. projects.Keys]), p => projects[p]).Update("/r", 0);

        store.Saved!.FanOut.Should().BeEquivalentTo(new Dictionary<string, int> { ["A"] = 2, ["B"] = 1, ["D"] = 3 });
        (result.Added, result.Lowered, result.Removed).Should().Be((1, 1, 1));
    }

    private sealed class FixedFiles(IReadOnlyList<string> files) : KirillMaster.QualityGates.Cli.Application.Ports.IPrFiles
    {
        public IReadOnlyList<string> Expand(IEnumerable<string> patterns) => files;

        public bool Exists(string path) => true;
    }

    private sealed class FanOutStore(Baseline loaded) : KirillMaster.QualityGates.Cli.Application.Ports.IBaselineStore
    {
        public Baseline? Saved { get; private set; }

        public Baseline? Load(string path) => loaded;

        public void Save(string path, Baseline baseline) => Saved = baseline;
    }

    [Fact]
    public void Transitive_fan_out_counts_the_closure_and_warns_over_the_limit()
    {
        var graph = ProjectGraph.Build(new Dictionary<string, string>
        {
            ["src/A/A.csproj"] = "<Project><ItemGroup><ProjectReference Include=\"..\\B\\B.csproj\" /></ItemGroup></Project>",
            ["src/B/B.csproj"] = "<Project><ItemGroup><ProjectReference Include=\"..\\C\\C.csproj\" /><ProjectReference Include=\"..\\A\\A.csproj\" /></ItemGroup></Project>",
            ["src/C/C.csproj"] = "<Project />"
        });

        var ok = TransitiveFanOutGate.Evaluate(graph, ["A"], 5);
        var over = TransitiveFanOutGate.Evaluate(graph, ["A"], 1);

        ok.Verdict.Should().Be(Verdict.Pass);
        ok.Actual.Should().Be(2);
        over.Verdict.Should().Be(Verdict.Warn);
        TransitiveFanOutGate.Evaluate(graph, ["Z"], 1).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Process_reports_list_churn_and_instability_and_never_fail()
    {
        var graph = ProjectGraph.Build(new Dictionary<string, string>
        {
            ["A/A.csproj"] = "<Project><ItemGroup><ProjectReference Include=\"..\\B\\B.csproj\" /></ItemGroup></Project>",
            ["B/B.csproj"] = "<Project />"
        });
        IReadOnlyList<IReadOnlyList<string>> history = [["A/X.cs"], ["A/X.cs", "B/Y.cs"]];

        var result = ProcessReportsGate.Evaluate(["A/X.cs"], history, graph, ["A", "B"]);

        result.Verdict.Should().Be(Verdict.Pass);
        result.Offenders.Should().Contain("churn: A/X.cs changed in 2 of the last 2 commit(s)")
            .And.Contain("instability: A I=1.00 (Ce=1, Ca=0)").And.Contain("instability: B I=0.00 (Ce=0, Ca=1)");
        ProcessReportsGate.Evaluate([], [], graph, []).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Changed_code_needs_changed_tests_unless_the_change_is_structural()
    {
        var code = new FileChange("src/A/A.cs", null, 3, 1);
        var test = new FileChange("tests/A.Tests/ATests.cs", null, 5, 0);

        ChangedCodeChangedTestsGate.Evaluate([code], ChangeKind.Behavioral).Verdict.Should().Be(Verdict.Fail);
        ChangedCodeChangedTestsGate.Evaluate([code], ChangeKind.Bugfix).Offenders.Should().Equal("src/A/A.cs");
        ChangedCodeChangedTestsGate.Evaluate([code, test], ChangeKind.Behavioral).Verdict.Should().Be(Verdict.Pass);
        ChangedCodeChangedTestsGate.Evaluate([code], ChangeKind.Structural).Verdict.Should().Be(Verdict.NotApplicable);
        ChangedCodeChangedTestsGate.Evaluate([new FileChange("src/A/Migrations/1.cs", null, 9, 0), new FileChange("README.md", null, 1, 0)], ChangeKind.Behavioral).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Feature_readme_fails_for_a_missing_or_stub_readme()
    {
        FeatureReadmeGate.Evaluate(["src/A/Features/Orders"], _ => null).Offenders.Should().Equal("src/A/Features/Orders: README.md is missing");
        FeatureReadmeGate.Evaluate(["src/A/Features/Orders"], _ => "TODO").Verdict.Should().Be(Verdict.Fail);
        FeatureReadmeGate.Evaluate(["src/A/Features/Orders"], _ => "Places orders and publishes the OrderPlaced event to the bus.").Verdict.Should().Be(Verdict.Pass);
        FeatureReadmeGate.Evaluate([], _ => null).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Theory]
    [InlineData("tests/A.Tests/ATests.cs", true)]
    [InlineData("src/A.UnitTests/ATests.cs", true)]
    [InlineData("src/A.IntegrationTests/Db/ATests.cs", true)]
    [InlineData("tests/Shared/Helper.cs", true)]
    [InlineData("src/A/Tests.cs", false)]
    [InlineData("src/A.Application/Orders.cs", false)]
    public void Test_paths_follow_the_layer_classifier_test_project_names(string path, bool isTest)
    {
        SourcePaths.IsTest(path).Should().Be(isTest);
        SourcePaths.IsProduction(path).Should().Be(!isTest);
        ChangedCodeChangedTestsGate.Evaluate([new FileChange("src/A/A.cs", null, 1, 0), new FileChange(path, null, 1, 0)], ChangeKind.Behavioral).Verdict
            .Should().Be(isTest ? Verdict.Pass : Verdict.Fail);
    }

    [Fact]
    public async Task Feature_readme_is_required_only_for_folders_that_are_new_in_the_pr()
    {
        var source = new FakeSource();
        source.Changes.Add(new FileChange("src/A/Features/Old/Handler.cs", null, 4, 0));
        source.Changes.Add(new FileChange("src/A/Features/Moved/Handler.cs", "src/A/Other/Handler.cs", 0, 0));
        source.Changes.Add(new FileChange("src/A/Features/New/Handler.cs", null, 4, 0));
        source.BaseFiles.AddRange(["src/A/Features/Old/Existing.cs", "src/A/Other/Handler.cs"]);
        var gates = new PrProcessGates(source, Insights, new FakeArtifacts(), TimeProvider.System);

        var results = await gates.EvaluateAsync(new PrRequest("/repo", "origin/develop", "feature/x", [], [], "/out", Process: new PrProcessOptions()));

        results.Single(r => r.Gate == GateId.FeatureReadme).Offenders.Should().Equal("src/A/Features/Moved: README.md is missing", "src/A/Features/New: README.md is missing");
    }

    [Fact]
    public async Task Source_files_are_read_with_one_batch_call_instead_of_one_read_per_file()
    {
        var source = new FakeSource();
        source.Files[(PrRevision.Head, "src/A/A.cs")] = "public class A { }";
        source.Files[(PrRevision.Head, "src/A/B.cs")] = "public class B { }";
        var gates = new PrProcessGates(source, Insights, new FakeArtifacts(), TimeProvider.System);

        await gates.EvaluateAsync(new PrRequest("/repo", "origin/develop", "feature/x", [], [], "/out", Process: new PrProcessOptions()));

        source.ReadManyCalls.Should().BeGreaterThan(0);
        source.ReadCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    [InlineData("{\"builds\":[{\"number\":\"x\",\"result\":\"SUCCESS\"},7,{\"result\":1}]}")]
    public async Task A_malformed_build_history_is_a_warning_never_an_exception(string json)
    {
        var artifacts = new FakeArtifacts();
        artifacts.Written["history.json"] = json;
        var gates = new PrProcessGates(new FakeSource(), Insights, artifacts, TimeProvider.System);

        var results = await gates.EvaluateAsync(new PrRequest("/repo", "origin/develop", "feature/x", [], [], "/out", Process: new PrProcessOptions(BuildHistory: "history.json")));

        var trend = results.Single(r => r.Gate == GateId.BuildTrend);
        trend.Verdict.Should().BeOneOf(Verdict.Warn, Verdict.NotApplicable);
        trend.Verdict.Should().NotBe(Verdict.Fail);

        var missing = await gates.EvaluateAsync(new PrRequest("/repo", "origin/develop", "feature/x", [], [], "/out", Process: new PrProcessOptions(BuildHistory: "absent.json")));
        missing.Single(r => r.Gate == GateId.BuildTrend).Verdict.Should().Be(Verdict.Warn);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_structural_change_without_base_coverage_falls_back_to_behavioral_and_says_so(bool withGlobs, bool withCommit)
    {
        var source = new FakeSource();
        source.Changes.Add(new FileChange("src/A/A.cs", null, 3, 1));
        var gates = new PrProcessGates(source, Insights, new FakeArtifacts(), TimeProvider.System, new PrProcessPorts(Files: new FakeFiles(), Coverage: new FakeCoverage()));
        var options = new PrProcessOptions(ChangeKind.Structural, BaseCoverage: withGlobs ? ["base.xml"] : null, BaseCoverageCommit: withCommit ? "abc" : null);

        var results = await gates.EvaluateAsync(new PrRequest("/repo", "origin/develop", "feature/x", ["head.xml"], [], "/out", Process: options));

        options.EffectiveKind.Should().Be(ChangeKind.Behavioral);
        results.Single(r => r.Gate == GateId.ChangedCodeChangedTests).Verdict.Should().Be(Verdict.Fail);
        var moved = results.Single(r => r.Gate == GateId.MovedLineCoverage);
        moved.Verdict.Should().Be(Verdict.Warn);
        moved.Message.Should().Contain("treated as behavioral");
    }

    [Fact]
    public void A_structural_change_with_base_coverage_and_commit_stays_structural()
    {
        new PrProcessOptions(ChangeKind.Structural, BaseCoverage: ["b.xml"], BaseCoverageCommit: "abc").EffectiveKind.Should().Be(ChangeKind.Structural);
        new PrProcessOptions(ChangeKind.Bugfix).EffectiveKind.Should().Be(ChangeKind.Bugfix);
    }

    [Fact]
    public async Task Moved_line_coverage_diffs_from_the_commit_the_base_coverage_was_built_at()
    {
        var source = new FakeSource();
        var ports = new PrProcessPorts(Files: new FakeFiles(), Coverage: new FakeCoverage());
        var gates = new PrProcessGates(source, Insights, new FakeArtifacts(), TimeProvider.System, ports);
        var request = new PrRequest("/repo", "origin/develop", "feature/x", ["head.xml"], [], "/out", Process: new PrProcessOptions(ChangeKind.Structural, BaseCoverage: ["base.xml"]));

        var missingCommit = await gates.EvaluateAsync(request);
        missingCommit.Single(r => r.Gate == GateId.MovedLineCoverage).Verdict.Should().Be(Verdict.Warn);
        source.DiffCommit.Should().BeNull();

        var results = await gates.EvaluateAsync(request with { Process = new PrProcessOptions(ChangeKind.Structural, BaseCoverage: ["base.xml"], BaseCoverageCommit: "abc123") });

        source.DiffCommit.Should().Be("abc123");
        results.Single(r => r.Gate == GateId.MovedLineCoverage).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Mutation_ratchet_warns_on_a_drop_and_reports_the_first_score()
    {
        MutantResult[] mutants = [new("A.cs", 1, "m", MutantStatus.Killed), new("A.cs", 2, "m", MutantStatus.Survived), new("B.cs", 3, "m", MutantStatus.Killed), new("B.cs", 4, "m", MutantStatus.Ignored)];

        MutationRatchetGate.Score(mutants).Should().BeApproximately(66.67, 0.01);
        MutationRatchetGate.Evaluate(mutants, 80).Verdict.Should().Be(Verdict.Warn);
        MutationRatchetGate.Evaluate(mutants, 60).Verdict.Should().Be(Verdict.Pass);
        MutationRatchetGate.Evaluate(mutants, null).Message.Should().Contain("becomes the baseline");
        MutationRatchetGate.Evaluate([new MutantResult("A.cs", 1, "m", MutantStatus.Ignored)], 50).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Mermaid_diagrams_mark_removed_edges_dashed_and_list_endpoint_calls()
    {
        MermaidDiagrams.ProjectEdges(["A -> B"], ["A -> C"]).Should().Contain("A[\"A\"] --> B[\"B\"]").And.Contain("A[\"A\"] -.-> C[\"C\"]");
        MermaidDiagrams.RequestFlows([new EndpointFlow("OrdersController.Create", ["IOrderService.Place"])]).Should().Contain("OrdersController_Create --> IOrderService_Place[\"IOrderService.Place\"]");
    }

    [Fact]
    public void Roslyn_insights_find_public_types_identifiers_and_flows()
    {
        const string Source = """
            namespace N {
                public class OrdersController {
                    private readonly IOrderService _orders;
                    public OrdersController(IOrderService orders) { _orders = orders; }
                    public void Create() { _orders.Place(); }
                    private void Hidden() { _orders.Cancel(); }
                }
                internal class Internal { }
                public interface IThing { }
            }
            """;

        Insights.PublicTypes(Source).Should().Equal("OrdersController", "IThing");
        Insights.Identifiers(Source).Should().Contain("IOrderService");
        Insights.Flows(Source).Should().ContainSingle().Which.Should().BeEquivalentTo(new EndpointFlow("OrdersController.Create", ["IOrderService.Place"]));
        Insights.Flows("app.MapGet(\"/o\", (IOrderService s) => s.List());").Should().ContainSingle().Which.Endpoint.Should().Be("GET /o");
        Insights.CognitiveComplexity("not C# at all {{{").Should().BeEmpty();
    }

    [Fact]
    public void Change_coupling_blocks_a_new_cluster_and_exempts_adapter_boundary_triples()
    {
        var options = new CouplingOptions(Commits: 100, MinSupport: 2, MinFileCommits: 2, MinConfidence: 0.8, MinClusterPartners: 2, BlockNewClusters: true);
        var recent = Enumerable.Repeat<IReadOnlyList<string>>(["src/A.cs", "src/B.cs", "src/C.cs"], 8);
        var old = Enumerable.Repeat<IReadOnlyList<string>>(["src/A.cs"], 2);
        IReadOnlyList<IReadOnlyList<string>> history = [.. recent, .. old];

        var blocked = ChangeCouplingGate.Evaluate(["src/A.cs"], history, _ => true, options);
        var warnOnly = ChangeCouplingGate.Evaluate(["src/A.cs"], history, _ => true, options with { BlockNewClusters = false });

        blocked.Verdict.Should().Be(Verdict.Fail);
        warnOnly.Verdict.Should().Be(Verdict.Warn);

        var boundary = Enumerable.Repeat<IReadOnlyList<string>>(["src/Ports.Output/IRepo.cs", "src/Adapters/Entities/E.cs", "src/Adapters/Mappers/M.cs"], 8).ToList();
        ChangeCouplingGate.Evaluate(["src/Ports.Output/IRepo.cs"], boundary, _ => true, options).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public async Task Process_gates_run_end_to_end_over_a_fake_source()
    {
        var source = new FakeSource();
        source.Changes.Add(new FileChange("src/A/Features/Orders/Handler.cs", null, 9, 0));
        source.Changes.Add(new FileChange("src/A/A.csproj", null, 3, 0));
        source.Files[(PrRevision.Head, "src/A/Features/Orders/Handler.cs")] = "// qg-toggle-expiry: 2020-01-01\npublic class Handler { public void H() { if (true) { } } }";
        source.Files[(PrRevision.Head, "src/A/A.csproj")] = "<Project />";
        var artifacts = new FakeArtifacts();
        var gates = new PrProcessGates(source, Insights, artifacts, TimeProvider.System);

        var results = await gates.EvaluateAsync(new PrRequest("/repo", "origin/develop", "feature/x", [], [], "/out", Process: new PrProcessOptions()));

        results.Single(r => r.Gate == GateId.CognitiveComplexityDelta).Verdict.Should().Be(Verdict.Pass);
        results.Single(r => r.Gate == GateId.FeatureReadme).Offenders.Should().Equal("src/A/Features/Orders: README.md is missing");
        results.Single(r => r.Gate == GateId.ChangedCodeChangedTests).Verdict.Should().Be(Verdict.Fail);
        results.Single(r => r.Gate == GateId.ToggleExpiry).Verdict.Should().Be(Verdict.Warn);
        results.Single(r => r.Gate == GateId.DeadPublic).Verdict.Should().Be(Verdict.Warn);
        results.Single(r => r.Gate == GateId.TransitiveFanOut).Verdict.Should().Be(Verdict.Pass);
        results.Single(r => r.Gate == GateId.ProcessReports).Verdict.Should().BeOneOf(Verdict.Pass, Verdict.NotApplicable);
        results.Single(r => r.Gate == GateId.ArchFlows).Verdict.Should().BeOneOf(Verdict.Pass, Verdict.NotApplicable);
    }

    [Fact]
    public async Task Process_gates_honour_skip_groups_and_fail_closed_when_the_blocking_input_breaks()
    {
        var source = new FakeSource();
        var gates = new PrProcessGates(source, Insights, new FakeArtifacts(), TimeProvider.System);
        QualityGroup[] all = [QualityGroup.Complexity, QualityGroup.Toggles, QualityGroup.DeadPublic, QualityGroup.FanOut, QualityGroup.Reports, QualityGroup.FeatureReadme, QualityGroup.Flows, QualityGroup.TestDiff, QualityGroup.IlGraph, QualityGroup.BranchAge, QualityGroup.BuildTrend, QualityGroup.MovedCoverage];

        var skipped = await gates.EvaluateAsync(new PrRequest("/repo", "t", "s", [], [], "/out", Skip: all));
        skipped.Should().HaveCount(13).And.OnlyContain(r => r.Verdict == Verdict.Skip);

        source.ChangesFailure = new InvalidOperationException("git broke");
        var act = () => gates.EvaluateAsync(new PrRequest("/repo", "t", "s", [], [], "/out"));
        await act.Should().ThrowAsync<PrInputException>().WithMessage("*git broke*");

        var advisory = await gates.EvaluateAsync(new PrRequest("/repo", "t", "s", [], [], "/out", Skip: [QualityGroup.Complexity, QualityGroup.FeatureReadme, QualityGroup.TestDiff]));
        advisory.Single(r => r.Gate == GateId.ToggleExpiry).Verdict.Should().Be(Verdict.Warn);
    }

    private sealed class FakeFiles : IPrFiles
    {
        public IReadOnlyList<string> Expand(IEnumerable<string> patterns) => [.. patterns];

        public bool Exists(string path) => true;
    }

    private sealed class FakeCoverage : ICoverageReader
    {
        public KirillMaster.QualityGates.Cli.Domain.Coverage.CoverageReport ReadMerged(IReadOnlyList<string> files, string repoRoot) => new();
    }

    private sealed class FakeArtifacts : IArtifactWriter
    {
        public Dictionary<string, string> Written { get; } = [];

        public void Write(string directory, string fileName, string content) => Written[fileName] = content;

        public string? Read(string path) => Written.GetValueOrDefault(path);
    }

    private sealed class FakeSource : IPrChangeSource
    {
        public List<FileChange> Changes { get; } = [];

        public Exception? ChangesFailure { get; set; }

        public Dictionary<(PrRevision, string), string> Files { get; } = [];

        public List<string> BaseFiles { get; } = [];

        public string? DiffCommit { get; private set; }

        public int ReadManyCalls { get; private set; }

        public int ReadCalls { get; private set; }

        public Task<IReadOnlyList<FileChange>> ChangesAsync(string repoPath, string target, CancellationToken cancellationToken = default) =>
            ChangesFailure is null ? Task.FromResult<IReadOnlyList<FileChange>>(Changes) : Task.FromException<IReadOnlyList<FileChange>>(ChangesFailure);

        public Task<string?> ReadAsync(string repoPath, string target, PrRevision revision, string path, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return Task.FromResult(Files.GetValueOrDefault((revision, path)));
        }

        public Task<IReadOnlyDictionary<string, string?>> ReadManyAsync(string repoPath, string target, PrRevision revision, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            ReadManyCalls++;
            return Task.FromResult<IReadOnlyDictionary<string, string?>>(paths.Distinct().ToDictionary(p => p, p => Files.GetValueOrDefault((revision, p))));
        }

        public Task<IReadOnlyList<string>> BaseFilesAsync(string repoPath, string target, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(BaseFiles);

        public Task<string> DiffFromAsync(string repoPath, string commit, CancellationToken cancellationToken = default)
        {
            DiffCommit = commit;
            return Task.FromResult(string.Empty);
        }

        public Task<IReadOnlyList<string>> HeadFilesAsync(string repoPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. Files.Keys.Where(k => k.Item1 == PrRevision.Head).Select(k => k.Item2)]);

        public Task<IReadOnlyList<IReadOnlyList<string>>> HistoryAsync(string repoPath, string target, int count, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>([["src/A/Features/Orders/Handler.cs"]]);
    }
}

public class StructureGatesTests
{
    private static AssemblyFacts Facts(string name, int types, int abstractTypes, string[] references, string[]? edges = null) =>
        new(name, types, abstractTypes, references.ToHashSet(), (edges ?? []).ToHashSet());

    [Fact]
    public void Compiled_edge_delta_lists_added_and_removed_edges_and_only_totals_without_a_base()
    {
        var head = new[] { Facts("A", 2, 0, ["B", "System"], ["A:A.Core -> B:B.Ports"]), Facts("B", 1, 1, []) };
        var baseline = new[] { Facts("A", 2, 0, []), Facts("B", 1, 1, []) };

        var delta = ArchDeltaIlGate.Evaluate(head, baseline);

        delta.Offenders.Should().Equal("+ A -> B", "+ A:A.Core -> B:B.Ports");
        ArchDeltaIlGate.Evaluate(head, baseline).Verdict.Should().Be(Verdict.Pass);
        ArchDeltaIlGate.Evaluate(head, null).Message.Should().Contain("delta not computed");
        ArchDeltaIlGate.Evaluate([], null).Verdict.Should().Be(Verdict.NotApplicable);
        ArchDeltaIlGate.Evaluate(head, head).Offenders.Should().BeEmpty();
    }

    [Fact]
    public void Main_sequence_reports_distance_from_abstractness_and_instability()
    {
        var facts = new[] { Facts("A", 4, 0, ["B"]), Facts("B", 2, 2, []) };

        var result = MainSequenceGate.Evaluate(facts);

        result.Offenders.Should().Equal("A: D=0.00 (A=0.00, I=1.00)", "B: D=0.00 (A=1.00, I=0.00)");
        MainSequenceGate.Evaluate([Facts("Z", 3, 0, [])]).Offenders.Should().Equal("Z: D=1.00 (A=0.00, I=0.00)");
        MainSequenceGate.Evaluate([]).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Branch_age_warns_over_the_limit_and_is_not_applicable_without_commits()
    {
        var now = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

        BranchAgeGate.Evaluate(now.AddDays(-2), now).Verdict.Should().Be(Verdict.Pass);
        BranchAgeGate.Evaluate(now.AddDays(-9), now).Verdict.Should().Be(Verdict.Warn);
        BranchAgeGate.Evaluate(null, now).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Build_trend_reads_the_build_history_document_and_needs_it()
    {
        BuildRecord[] builds = [new(3, "FAILURE", 30000), new(2, "SUCCESS", 20000), new(1, "SUCCESS", 10000)];

        var result = BuildTrendGate.Evaluate(builds);

        result.Offenders.Should().Equal("green: 2 of 3 builds", "duration: 10s (older half) -> 30s (recent half)");
        BuildTrendGate.Evaluate(null).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Diff_headers_are_only_recognised_before_the_first_hunk()
    {
        const string Diff = "diff --git a/src/Old.cs b/src/Old.cs\n--- a/src/Old.cs\n+++ b/src/Old.cs\n@@ -1,2 +1,2 @@\n--- a/src/Fake.cs\n+++ b/src/Fake.cs\n context line here\n"
            + "diff --git a/src/New.cs b/src/New.cs\n--- a/src/New.cs\n+++ b/src/New.cs\n@@ -0,0 +1,2 @@\n+-- a/src/Fake.cs\n+++ b/src/Fake.cs\n";

        var moved = MovedLines.Find(Diff);

        moved.Should().ContainSingle().Which.Should().Be(new MovedLine("src/Old.cs", 1, "src/New.cs", 1));
    }

    [Fact]
    public void Moved_lines_are_found_across_files_and_a_lost_coverage_fails()
    {
        const string Diff = """
            diff --git a/src/Old.cs b/src/Old.cs
            --- a/src/Old.cs
            +++ b/src/Old.cs
            @@ -3,2 +3,0 @@
            -    var total = Compute(order);
            -    return total;
            diff --git a/src/New.cs b/src/New.cs
            --- a/src/New.cs
            +++ b/src/New.cs
            @@ -0,0 +10,2 @@
            +    var total = Compute(order);
            +    return total;
            """;

        var moved = MovedLines.Find(Diff);

        moved.Should().Equal(new MovedLine("src/Old.cs", 3, "src/New.cs", 10), new MovedLine("src/Old.cs", 4, "src/New.cs", 11));
        MovedLineCoverageGate.Evaluate(moved, null, null).Verdict.Should().Be(Verdict.NotApplicable);

        var before = new KirillMaster.QualityGates.Cli.Domain.Coverage.CoverageReport();
        before.Record("src/Old.cs", 3, 2);
        before.Record("src/Old.cs", 4, 2);
        var covered = new KirillMaster.QualityGates.Cli.Domain.Coverage.CoverageReport();
        covered.Record("src/New.cs", 10, 1);
        covered.Record("src/New.cs", 11, 1);
        var lost = new KirillMaster.QualityGates.Cli.Domain.Coverage.CoverageReport();
        lost.Record("src/New.cs", 10, 1);
        lost.Record("src/New.cs", 11, 0);

        MovedLineCoverageGate.Evaluate(moved, before, covered).Verdict.Should().Be(Verdict.Pass);
        MovedLineCoverageGate.Evaluate(moved, before, lost).Offenders.Should().Equal("src/New.cs:11 moved from src/Old.cs:4 was covered, now is not");
        MovedLineCoverageGate.Evaluate(moved, new KirillMaster.QualityGates.Cli.Domain.Coverage.CoverageReport(), lost).Verdict.Should().Be(Verdict.Warn);
        MovedLineCoverageGate.Evaluate(moved, new KirillMaster.QualityGates.Cli.Domain.Coverage.CoverageReport(), lost).Message.Should().Contain("--base-coverage-root");
        MovedLineCoverageGate.Evaluate([], before, lost).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Metadata_reader_reads_the_types_references_and_namespace_edges_of_real_assemblies()
    {
        var assembly = typeof(ArchDeltaGate).Assembly.Location;

        var facts = new KirillMaster.QualityGates.Cli.Adapters.Roslyn.MetadataAssemblyReader().Read([assembly, "does-not-exist.dll"]);

        var cli = facts.Should().ContainSingle().Subject;
        cli.Name.Should().Be("KirillMaster.QualityGates.Cli");
        cli.References.Should().Contain("System.CommandLine");
        cli.AbstractTypes.Should().BeGreaterThan(0);
    }
}

public sealed class MutationRatchetCliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("qg-ratchet").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (int Exit, string Out, string Err) Run(string reportJson, string? baselineJson)
    {
        var report = Path.Combine(_dir, "mutation-report.json");
        File.WriteAllText(report, reportJson);
        var baseline = Path.Combine(_dir, "baseline.json");
        if (baselineJson is not null)
        {
            File.WriteAllText(baseline, baselineJson);
        }

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = KirillMaster.QualityGates.Cli.CliHost.Run(["mutation-ratchet", "--repo", _dir, "--report", report, "--baseline", baseline, "--out", Path.Combine(_dir, "out")], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private const string Report = """{"files":{"A.cs":{"mutants":[{"status":"Killed","location":{"start":{"line":1}},"mutatorName":"m"},{"status":"Survived","location":{"start":{"line":2}},"mutatorName":"m"}]}}}""";

    [Fact]
    public void A_score_below_the_baseline_exits_two_and_a_better_one_proposes_a_new_baseline()
    {
        Run(Report, """{"score": 80}""").Exit.Should().Be(2);
        File.Exists(Path.Combine(_dir, "out", "mutation-baseline.json")).Should().BeFalse();

        Run(Report, """{"score": 40}""").Exit.Should().Be(0);
        File.ReadAllText(Path.Combine(_dir, "out", "mutation-baseline.json")).Should().Contain("50");
    }

    [Fact]
    public void An_unreadable_report_or_baseline_exits_three()
    {
        Run("not json", null).Exit.Should().Be(3);
        Run(Report, "{}").Exit.Should().Be(3);
    }
}
