using FluentAssertions;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Application.UseCases;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class DeltaGatesTests
{
    private static IReadOnlyDictionary<string, int> Counts(params (string Key, int Count)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Count);

    private static readonly IReadOnlySet<string> Owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alice" };

    [Fact]
    public void Baseline_growth_passes_when_counts_only_shrink()
    {
        var delta = new BaselineDelta("b.json", Counts(("a", 3), ("b", 1)), Counts(("a", 2)));

        BaselineGrowthGate.Evaluate([delta], [], Owners).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void Baseline_growth_fails_on_a_larger_count_and_a_brand_new_key()
    {
        var grown = new BaselineDelta("b.json", Counts(("a", 1)), Counts(("a", 2)));
        var added = new BaselineDelta("c.json", Counts(), Counts(("x", 1)));

        var result = BaselineGrowthGate.Evaluate([grown, added], [], Owners);

        result.Verdict.Should().Be(Verdict.Fail);
        result.Offenders.Should().Equal("b.json: a 1 -> 2", "c.json: x new (1)");
    }

    [Fact]
    public void Baseline_growth_treats_a_key_rename_with_an_equal_total_as_a_pass()
    {
        var delta = new BaselineDelta("b.json", Counts(("old/File.cs:M", 2)), Counts(("new/File.cs:M", 2)));

        BaselineGrowthGate.Evaluate([delta], [], Owners).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void Baseline_growth_fails_when_a_rename_also_adds_to_the_total()
    {
        var delta = new BaselineDelta("b.json", Counts(("old", 1)), Counts(("new", 2)));

        BaselineGrowthGate.Evaluate([delta], [], Owners).Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public void Baseline_growth_warns_when_an_independent_code_owner_approved_it_case_insensitively()
    {
        var delta = new BaselineDelta("b.json", Counts(("a", 1)), Counts(("a", 2)));

        var result = BaselineGrowthGate.Evaluate([delta], ["Alice"], Owners, "bob");

        result.Verdict.Should().Be(Verdict.Warn);
        result.Offenders.Should().HaveCount(1);
    }

    [Fact]
    public void Baseline_growth_still_fails_when_the_approver_is_not_a_code_owner()
    {
        var delta = new BaselineDelta("b.json", Counts(("a", 1)), Counts(("a", 2)));

        var result = BaselineGrowthGate.Evaluate([delta], ["mallory"], Owners, "bob");

        result.Verdict.Should().Be(Verdict.Fail);
        result.Message.Should().Contain("mallory");
    }

    [Fact]
    public void Baseline_growth_rejects_an_approver_who_is_the_pr_author()
    {
        var delta = new BaselineDelta("b.json", Counts(("a", 1)), Counts(("a", 2)));

        var result = BaselineGrowthGate.Evaluate([delta], ["alice"], Owners, "Alice");

        result.Verdict.Should().Be(Verdict.Fail);
        result.Message.Should().Contain("author");
    }

    [Fact]
    public void Baseline_growth_rejects_any_approver_without_a_pr_author()
    {
        var delta = new BaselineDelta("b.json", Counts(("a", 1)), Counts(("a", 2)));

        BaselineGrowthGate.Evaluate([delta], ["alice"], Owners).Message.Should().Contain("--pr-author");
        BaselineGrowthGate.Evaluate([delta], ["alice"], Owners, " ").Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public void Baseline_growth_accepts_a_later_independent_owner_when_the_first_approver_is_the_author()
    {
        var delta = new BaselineDelta("b.json", Counts(("a", 1)), Counts(("a", 2)));
        IReadOnlySet<string> owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alice", "carol" };

        BaselineGrowthGate.Evaluate([delta], ["alice", "carol"], owners, "alice").Verdict.Should().Be(Verdict.Warn);
    }

    [Fact]
    public void Code_owners_lists_user_logins_and_skips_teams_comments_and_emails()
    {
        var logins = CodeOwners.Logins("# header\n* @Alice @org/team dev@x.io\n/docs/ @bob # trailing\n");

        logins.Should().BeEquivalentTo("Alice", "bob");
    }

    [Fact]
    public void Baseline_json_counts_only_warnings_and_grandfathered_sections()
    {
        var json = "{\"commit\":\"1\",\"projects\":[{\"name\":\"A\",\"lineCoverage\":90}],\"warnings\":{\"A\":{\"CA1\":2}},\"grandfathered\":{\"A\":{\"RS0030\":1}}}";

        BaselineCounts.Parse("quality/baseline.json", json).Should().BeEquivalentTo(new Dictionary<string, int> { ["warnings/A/CA1"] = 2, ["grandfathered/A/RS0030"] = 1 });
    }

    [Fact]
    public void Architecture_baseline_flattens_numbers_and_string_arrays()
    {
        var json = "{\"rules\":{\"P5\":3},\"violations\":[\"A->B\",\"A->C\"]}";

        BaselineCounts.Parse("quality/architecture-baseline.json", json).Should().BeEquivalentTo(
            new Dictionary<string, int> { ["rules/P5"] = 3, ["violations[A->B]"] = 1, ["violations[A->C]"] = 1 });
    }

    [Fact]
    public void Global_suppressions_count_one_per_target()
    {
        var text = "[assembly: SuppressMessage(\"a\", \"RS0030\", Scope = \"member\", Target = \"~M:A.B\")]\n[assembly: SuppressMessage(\"a\", \"RS0030\", Scope = \"member\", Target = \"~M:A.C\")]\n";

        BaselineCounts.Parse("quality/grandfathered/X.GlobalSuppressions.cs", text).Should().BeEquivalentTo(new Dictionary<string, int> { ["~M:A.B"] = 1, ["~M:A.C"] = 1 });
    }

    [Theory]
    [InlineData("quality/baseline.json", "quality/baseline.json", true)]
    [InlineData("quality/**/*baseline*.json", "quality/arch/x-baseline.json", true)]
    [InlineData("**/*architecture-baseline*.json", "src/a/architecture-baseline.json", true)]
    [InlineData("*.lock", "deep/dir/x.lock", true)]
    [InlineData("docs/**", "src/docs/a.md", false)]
    [InlineData("quality/*.json", "quality/a/b.json", false)]
    public void Path_glob_matches_gitattributes_style_patterns(string pattern, string path, bool expected)
    {
        PathGlob.IsMatch(pattern, path).Should().Be(expected);
    }

    [Fact]
    public void Linguist_generated_patterns_honour_unsetting()
    {
        var attributes = "* text=auto\n# c\n*.g.cs linguist-generated\nsrc/gen/** linguist-generated=true\nold/** linguist-generated\nold/** -linguist-generated\n";

        LinguistGenerated.Patterns(attributes).Should().Equal("*.g.cs", "src/gen/**");
    }

    [Fact]
    public void Pr_size_ignores_generated_lock_baseline_and_migration_designer_files()
    {
        FileChange[] changes =
        [
            new("src/A.cs", null, 100, 50),
            new("src/Api.g.cs", null, 9000, 0),
            new("package-lock.json", null, 500, 500),
            new("quality/baseline.json", null, 40, 40),
            new("src/Data/Migrations/20260101_Init.Designer.cs", null, 700, 0),
            new("src/Data/Migrations/20260101_Init.cs", null, 20, 0)
        ];

        var result = PrSizeGate.Evaluate(changes, ["*.g.cs"], new PrSizeLimits());

        result.Verdict.Should().Be(Verdict.Pass);
        result.Actual.Should().Be(170);
    }

    [Fact]
    public void Pr_size_warns_above_the_line_limit_and_lists_the_largest_files()
    {
        FileChange[] changes = [new("src/Big.cs", null, 390, 20), new("src/Small.cs", null, 1, 0)];

        var result = PrSizeGate.Evaluate(changes, [], new PrSizeLimits());

        result.Verdict.Should().Be(Verdict.Warn);
        result.Offenders!.First().Should().StartWith("src/Big.cs");
    }

    [Fact]
    public void Pr_size_warns_above_the_file_limit_and_honours_custom_limits()
    {
        var many = Enumerable.Range(0, 21).Select(i => new FileChange($"src/F{i}.cs", null, 1, 0)).ToList();

        PrSizeGate.Evaluate(many, [], new PrSizeLimits()).Verdict.Should().Be(Verdict.Warn);
        PrSizeGate.Evaluate(many, [], new PrSizeLimits(MaxFiles: 30)).Verdict.Should().Be(Verdict.Pass);
        PrSizeGate.Evaluate(many, [], new PrSizeLimits(MaxLines: 5, MaxFiles: 30)).Verdict.Should().Be(Verdict.Warn);
    }

    private static IReadOnlyList<IReadOnlyList<string>> Pairs(int together, int alone = 0, params string[] extra)
    {
        var history = new List<IReadOnlyList<string>>();
        for (var i = 0; i < together; i++)
        {
            history.Add(["src/A.cs", "src/B.cs", .. extra]);
        }

        for (var i = 0; i < alone; i++)
        {
            history.Add(["src/A.cs"]);
        }

        return history;
    }

    [Fact]
    public void Coupling_warns_for_a_strong_partner_missing_from_the_pr()
    {
        var result = ChangeCouplingGate.Evaluate(["src/A.cs"], Pairs(9, 1), _ => true, new CouplingOptions());

        result.Verdict.Should().Be(Verdict.Warn);
        result.Offenders.Should().Equal("src/A.cs usually changes with src/B.cs (90%, n=10)");
    }

    [Fact]
    public void Coupling_passes_when_the_partner_is_in_the_pr_deleted_or_below_thresholds()
    {
        ChangeCouplingGate.Evaluate(["src/A.cs", "src/B.cs"], Pairs(9, 1), _ => true, new CouplingOptions()).Verdict.Should().Be(Verdict.Pass);
        ChangeCouplingGate.Evaluate(["src/A.cs"], Pairs(9, 1), _ => false, new CouplingOptions()).Verdict.Should().Be(Verdict.Pass);
        ChangeCouplingGate.Evaluate(["src/A.cs"], Pairs(5, 5), _ => true, new CouplingOptions()).Verdict.Should().Be(Verdict.Pass);
        ChangeCouplingGate.Evaluate(["src/A.cs"], Pairs(4, 0), _ => true, new CouplingOptions()).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void Coupling_ignores_mass_commits_and_never_fails()
    {
        var mass = Pairs(9, 0, Enumerable.Range(0, 60).Select(i => $"src/M{i}.cs").ToArray());

        ChangeCouplingGate.Evaluate(["src/A.cs"], mass, _ => true, new CouplingOptions()).Verdict.Should().Be(Verdict.NotApplicable);
        ChangeCouplingGate.Evaluate(["src/A.cs"], [], _ => true, new CouplingOptions()).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public void Arch_delta_lists_added_and_removed_edges()
    {
        var before = "<Project><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /><ProjectReference Include=\"..\\Old\\Old.csproj\" /></ItemGroup></Project>";
        var after = "<Project><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /><ProjectReference Include=\"../Ports/Ports.csproj\" /></ItemGroup></Project>";

        var result = ArchDeltaGate.Evaluate(new Dictionary<string, (string?, string?)> { ["src/Adapters/Adapters.csproj"] = (before, after) });

        result.Verdict.Should().Be(Verdict.Pass);
        result.Offenders.Should().Equal("+ Adapters -> Ports", "- Adapters -> Old");
    }

    [Fact]
    public void Arch_delta_is_not_applicable_without_project_changes_and_tolerates_bad_xml()
    {
        ArchDeltaGate.Evaluate(new Dictionary<string, (string?, string?)>()).Verdict.Should().Be(Verdict.NotApplicable);
        ArchDeltaGate.Evaluate(new Dictionary<string, (string?, string?)> { ["a/A.csproj"] = ("<Project>", null) }).Offenders.Should().BeEmpty();
    }

    private sealed class FakeSource : IPrChangeSource
    {
        public List<FileChange> Changes { get; } = [];

        public Exception? ChangesFailure { get; set; }

        public Exception? HistoryFailure { get; set; }

        public Exception? ReadFailure { get; set; }

        public Dictionary<(PrRevision, string), string> Files { get; } = [];

        public List<IReadOnlyList<string>> History { get; } = [];

        public Task<IReadOnlyList<FileChange>> ChangesAsync(string repoPath, string target, CancellationToken cancellationToken = default) =>
            ChangesFailure is null ? Task.FromResult<IReadOnlyList<FileChange>>(Changes) : Task.FromException<IReadOnlyList<FileChange>>(ChangesFailure);

        public Task<string?> ReadAsync(string repoPath, string target, PrRevision revision, string path, CancellationToken cancellationToken = default) =>
            ReadFailure is null ? Task.FromResult(Files.GetValueOrDefault((revision, path))) : Task.FromException<string?>(ReadFailure);

        public Task<IReadOnlyDictionary<string, string?>> ReadManyAsync(string repoPath, string target, PrRevision revision, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
            ReadFailure is null ? Task.FromResult<IReadOnlyDictionary<string, string?>>(paths.Distinct().ToDictionary(p => p, p => Files.GetValueOrDefault((revision, p)))) : Task.FromException<IReadOnlyDictionary<string, string?>>(ReadFailure);

        public Task<IReadOnlyList<string>> BaseFilesAsync(string repoPath, string target, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string> DiffFromAsync(string repoPath, string commit, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<IReadOnlyList<string>> HeadFilesAsync(string repoPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["src/A.cs", "src/B.cs"]);

        public Task<IReadOnlyList<IReadOnlyList<string>>> HistoryAsync(string repoPath, string target, int count, CancellationToken cancellationToken = default) =>
            HistoryFailure is null ? Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>(History) : Task.FromException<IReadOnlyList<IReadOnlyList<string>>>(HistoryFailure);
    }

    private static PrRequest Request(PrDeltaOptions? delta = null, params QualityGroup[] skip) =>
        new("/repo", "origin/develop", "feature/x", [], [], "/out", Skip: skip, Delta: delta);

    [Fact]
    public async Task Runner_merges_suppression_files_into_one_group_so_a_moved_suppression_passes()
    {
        var source = new FakeSource();
        source.Changes.Add(new FileChange("quality/grandfathered/B.GlobalSuppressions.cs", null, 1, 0));
        source.Changes.Add(new FileChange("quality/grandfathered/A.GlobalSuppressions.cs", null, 0, 1));
        source.Files[(PrRevision.Base, "quality/grandfathered/A.GlobalSuppressions.cs")] = "[assembly: SuppressMessage(\"a\", \"b\", Target = \"~M:X\")]";
        source.Files[(PrRevision.Head, "quality/grandfathered/B.GlobalSuppressions.cs")] = "[assembly: SuppressMessage(\"a\", \"b\", Target = \"~M:X\")]";

        var results = await new PrDeltaGates(source).EvaluateAsync(Request());

        results.Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public async Task Runner_reads_owners_from_the_target_branch_and_ignores_commit_trailers()
    {
        var source = new FakeSource();
        source.Changes.Add(new FileChange("quality/baseline.json", null, 1, 1));
        source.Files[(PrRevision.Base, "quality/baseline.json")] = "{\"warnings\":{\"P\":{\"R\":1}}}";
        source.Files[(PrRevision.Head, "quality/baseline.json")] = "{\"warnings\":{\"P\":{\"R\":2}}}";
        var approved = new PrDeltaOptions(ApprovedBy: ["alice"], Author: "bob");

        (await new PrDeltaGates(source).EvaluateAsync(Request(approved))).Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Fail);

        source.Files[(PrRevision.Target, ".github/CODEOWNERS")] = "* @alice";
        (await new PrDeltaGates(source).EvaluateAsync(Request(approved))).Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Warn);
        (await new PrDeltaGates(source).EvaluateAsync(Request(new PrDeltaOptions(ApprovedBy: ["alice"], Author: "alice")))).Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Fail);
        (await new PrDeltaGates(source).EvaluateAsync(Request())).Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public async Task Runner_names_the_file_when_a_baseline_is_malformed_json()
    {
        var source = new FakeSource();
        source.Changes.Add(new FileChange("quality/baseline.json", null, 1, 1));
        source.Files[(PrRevision.Base, "quality/baseline.json")] = "{\"warnings\":{}}";
        source.Files[(PrRevision.Head, "quality/baseline.json")] = "{\"warnings\":";

        var act = () => new PrDeltaGates(source).EvaluateAsync(Request());

        (await act.Should().ThrowAsync<PrInputException>()).Which.Message.Should().Contain("quality/baseline.json").And.Contain("malformed JSON");
    }

    [Fact]
    public async Task Runner_honours_custom_baseline_globs()
    {
        var source = new FakeSource();
        source.Changes.Add(new FileChange("tools/debt.json", null, 1, 1));
        source.Files[(PrRevision.Base, "tools/debt.json")] = "{\"a\":1}";
        source.Files[(PrRevision.Head, "tools/debt.json")] = "{\"a\":2}";

        (await new PrDeltaGates(source).EvaluateAsync(Request())).Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Pass);
        (await new PrDeltaGates(source).EvaluateAsync(Request(new PrDeltaOptions(BaselineGlobs: ["tools/*.json"])))).Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public async Task Runner_bypasses_every_skipped_group()
    {
        var source = new FakeSource();

        var results = await new PrDeltaGates(source).EvaluateAsync(Request(null, QualityGroup.BaselineGrowth, QualityGroup.Coupling, QualityGroup.PrSize, QualityGroup.ArchDelta));

        results.Should().HaveCount(4).And.OnlyContain(r => r.Verdict == Verdict.Skip);
    }

    [Fact]
    public async Task Runner_excludes_gitattributes_generated_files_from_size_and_never_fails_on_hints()
    {
        var source = new FakeSource();
        source.Changes.Add(new FileChange("src/Gen.cs", null, 5000, 0));
        source.Changes.Add(new FileChange("src/A.cs", null, 10, 0));
        source.Files[(PrRevision.Target, ".gitattributes")] = "src/Gen.cs linguist-generated";
        source.History.AddRange(Pairs(9, 1));

        var results = await new PrDeltaGates(source).EvaluateAsync(Request());

        results.Single(r => r.Gate == GateId.PrSize).Verdict.Should().Be(Verdict.Pass);
        results.Single(r => r.Gate == GateId.ChangeCoupling).Verdict.Should().Be(Verdict.Warn);
        results.Should().NotContain(r => r.Verdict == Verdict.Fail);
    }

    [Fact]
    public async Task Runner_turns_a_git_failure_in_an_advisory_gate_into_a_warning_and_keeps_the_other_results()
    {
        var source = new FakeSource { HistoryFailure = new InvalidOperationException("git log failed (128): fatal: bad revision") };
        source.Changes.Add(new FileChange("src/A.cs", null, 10, 0));

        var results = await new PrDeltaGates(source).EvaluateAsync(Request());

        var coupling = results.Single(r => r.Gate == GateId.ChangeCoupling);
        coupling.Verdict.Should().Be(Verdict.Warn);
        coupling.Message.Should().Contain("git log failed");
        results.Single(r => r.Gate == GateId.PrSize).Verdict.Should().Be(Verdict.Pass);
        results.Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Pass);
        results.Single(r => r.Gate == GateId.ArchDelta).Verdict.Should().Be(Verdict.NotApplicable);
    }

    [Fact]
    public async Task Runner_warns_every_advisory_gate_and_fails_the_blocking_one_when_the_change_list_is_unavailable()
    {
        var source = new FakeSource { ChangesFailure = new InvalidOperationException("git diff failed (128): unknown revision") };
        var skipBlocking = new[] { QualityGroup.BaselineGrowth };

        var results = await new PrDeltaGates(source).EvaluateAsync(Request(null, skipBlocking));

        results.Where(r => r.Gate != GateId.BaselineGrowth).Should().OnlyContain(r => r.Verdict == Verdict.Warn && r.Message!.Contains("unknown revision"));
        var blocking = () => new PrDeltaGates(source).EvaluateAsync(Request());
        (await blocking.Should().ThrowAsync<PrInputException>()).Which.Message.Should().Contain("BaselineGrowth could not run").And.Contain("unknown revision");
    }

    [Fact]
    public async Task Runner_reports_a_read_failure_in_the_baseline_gate_as_a_tool_error_and_in_arch_delta_as_a_warning()
    {
        var source = new FakeSource { ReadFailure = new InvalidOperationException("git show failed (128): fatal: invalid object name") };
        source.Changes.Add(new FileChange("quality/baseline.json", null, 1, 1));
        source.Changes.Add(new FileChange("src/A/A.csproj", null, 1, 1));

        var blocking = () => new PrDeltaGates(source).EvaluateAsync(Request());
        await blocking.Should().ThrowAsync<PrInputException>();

        var results = await new PrDeltaGates(source).EvaluateAsync(Request(null, QualityGroup.BaselineGrowth));
        results.Single(r => r.Gate == GateId.ArchDelta).Verdict.Should().Be(Verdict.Warn);
        results.Single(r => r.Gate == GateId.PrSize).Verdict.Should().Be(Verdict.Warn);
    }

    [Fact]
    public void Baseline_growth_fails_for_a_removed_baseline_unless_an_independent_owner_approved()
    {
        var shrunk = new BaselineDelta("quality/baseline.json", Counts(("a", 1)), Counts());

        BaselineGrowthGate.Evaluate([shrunk], [], Owners, "bob", ["quality/baseline.json"]).Offenders.Should().Equal("quality/baseline.json: baseline removed");
        BaselineGrowthGate.Evaluate([shrunk], [], Owners, "bob", ["quality/baseline.json"]).Verdict.Should().Be(Verdict.Fail);
        BaselineGrowthGate.Evaluate([shrunk], ["alice"], Owners, "bob", ["quality/baseline.json"]).Verdict.Should().Be(Verdict.Warn);
        BaselineGrowthGate.Evaluate([shrunk], ["bob"], Owners, "bob", ["quality/baseline.json"]).Verdict.Should().Be(Verdict.Fail);
        BaselineGrowthGate.Evaluate([shrunk], [], Owners, "bob").Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public async Task Runner_fails_a_deleted_json_baseline()
    {
        var source = new FakeSource();
        source.Changes.Add(new FileChange("quality/baseline.json", null, 0, 9));
        source.Files[(PrRevision.Base, "quality/baseline.json")] = "{\"warnings\":{\"P\":{\"R\":1}}}";

        var result = (await new PrDeltaGates(source).EvaluateAsync(Request())).Single(r => r.Gate == GateId.BaselineGrowth);

        result.Verdict.Should().Be(Verdict.Fail);
        result.Offenders.Should().Equal("quality/baseline.json: baseline removed");
    }

    [Fact]
    public async Task Runner_fails_a_baseline_renamed_outside_the_globs_and_compares_a_rename_between_matching_paths()
    {
        var source = new FakeSource();
        source.Changes.Add(new FileChange("docs/old-baseline.txt", "quality/baseline.json", 0, 0));
        source.Files[(PrRevision.Base, "quality/baseline.json")] = "{\"warnings\":{\"P\":{\"R\":1}}}";
        source.Files[(PrRevision.Head, "docs/old-baseline.txt")] = "{\"warnings\":{\"P\":{\"R\":1}}}";

        (await new PrDeltaGates(source).EvaluateAsync(Request())).Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Fail);

        var between = new FakeSource();
        between.Changes.Add(new FileChange("quality/arch/architecture-baseline.json", "quality/architecture-baseline.json", 0, 0));
        between.Files[(PrRevision.Base, "quality/architecture-baseline.json")] = "{\"rules\":{\"P5\":3}}";
        between.Files[(PrRevision.Head, "quality/arch/architecture-baseline.json")] = "{\"rules\":{\"P5\":3}}";
        (await new PrDeltaGates(between).EvaluateAsync(Request())).Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Pass);

        between.Files[(PrRevision.Head, "quality/arch/architecture-baseline.json")] = "{\"rules\":{\"P5\":4}}";
        (await new PrDeltaGates(between).EvaluateAsync(Request())).Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public async Task Runner_allows_a_deleted_suppressions_file_because_it_only_shrinks()
    {
        var source = new FakeSource();
        source.Changes.Add(new FileChange("quality/grandfathered/A.GlobalSuppressions.cs", null, 0, 1));
        source.Files[(PrRevision.Base, "quality/grandfathered/A.GlobalSuppressions.cs")] = "[assembly: SuppressMessage(\"a\", \"b\", Target = \"~M:X\")]";

        (await new PrDeltaGates(source).EvaluateAsync(Request())).Single(r => r.Gate == GateId.BaselineGrowth).Verdict.Should().Be(Verdict.Pass);
    }
}
