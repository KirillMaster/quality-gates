using FluentAssertions;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Application.UseCases;
using KirillMaster.QualityGates.Cli.Domain.Diff;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public class NewCodeViolationTests
{
    private const string Diff = "diff --git a/src/Domain/Calc.cs b/src/Domain/Calc.cs\n--- a/src/Domain/Calc.cs\n+++ b/src/Domain/Calc.cs\n@@ -7,0 +8,2 @@\n+x\n+y\n";

    private static ChangedLines Changed() => DiffParser.Parse(Diff);

    private static Finding At(int line, string uri = "file:///repo/src/Domain/Calc.cs", string rule = "CA1822") =>
        new("Sample.Domain", rule, uri, line);

    [Fact]
    public void Violation_on_changed_line_fails_with_path_line_and_rule()
    {
        var result = NewCodeViolationGate.Evaluate(Changed(), [At(9)], "/repo");

        result.Gate.Should().Be(GateId.NewCodeViolations);
        result.Verdict.Should().Be(Verdict.Fail);
        result.Actual.Should().Be(1);
        result.Threshold.Should().Be(0);
        result.Offenders.Should().Equal("src/Domain/Calc.cs:9 CA1822");
    }

    [Fact]
    public void Violation_on_unchanged_line_of_changed_file_passes()
    {
        NewCodeViolationGate.Evaluate(Changed(), [At(3), At(10)], "/repo").Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void Violation_in_untouched_file_passes()
    {
        NewCodeViolationGate.Evaluate(Changed(), [At(8, "file:///repo/src/Other.cs")], "/repo").Verdict.Should().Be(Verdict.Pass);
    }

    [Theory]
    [InlineData("file:///repo/src/Domain/Calc.cs", "/repo")]
    [InlineData("file:///C:/ws/repo/src/Domain/Calc.cs", "C:\\ws\\repo")]
    [InlineData("C:\\ws\\repo\\src\\Domain\\Calc.cs", "C:/ws/repo/")]
    [InlineData("file:///src/Domain/Calc.cs", "/somewhere/else")]
    public void Paths_are_normalized_across_schemes_separators_and_roots(string uri, string root)
    {
        var result = NewCodeViolationGate.Evaluate(Changed(), [At(8, uri)], root);

        result.Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public void Root_level_changed_file_does_not_match_a_same_named_file_elsewhere()
    {
        var changed = DiffParser.Parse("diff --git a/Program.cs b/Program.cs\n--- a/Program.cs\n+++ b/Program.cs\n@@ -1,0 +2,1 @@\n+x\n");

        NewCodeViolationGate.Evaluate(changed, [At(2, "file:///build/src/Tool/Program.cs")], "/repo").Verdict.Should().Be(Verdict.Pass);
        NewCodeViolationGate.Evaluate(changed, [At(2, "Program.cs")], "/repo").Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public void Longest_changed_path_wins_the_suffix_match()
    {
        var diff = Diff + Diff.Replace("src/Domain/Calc.cs", "Domain/Calc.cs", StringComparison.Ordinal).Replace("+8,2", "+20,1", StringComparison.Ordinal);

        var result = NewCodeViolationGate.Evaluate(DiffParser.Parse(diff), [At(8, "file:///build/src/Domain/Calc.cs")], "/repo");

        result.Offenders.Should().Equal("src/Domain/Calc.cs:8 CA1822");
    }

    [Fact]
    public void Escaped_uri_characters_are_decoded()
    {
        var diff = Diff.Replace("Calc.cs", "My Calc.cs", StringComparison.Ordinal);

        var result = NewCodeViolationGate.Evaluate(DiffParser.Parse(diff), [At(8, "file:///src/Domain/My%20Calc.cs")], "/somewhere/else");

        result.Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public void Crlf_diff_still_maps_changed_lines()
    {
        var result = NewCodeViolationGate.Evaluate(DiffParser.Parse(Diff.Replace("\n", "\r\n", StringComparison.Ordinal)), [At(8)], "/repo");

        result.Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public void Offenders_are_sorted_and_deduplicated()
    {
        var result = NewCodeViolationGate.Evaluate(Changed(), [At(9, rule: "S1"), At(8, rule: "CA1"), At(9, rule: "S1")], "/repo");

        result.Offenders.Should().Equal("src/Domain/Calc.cs:8 CA1", "src/Domain/Calc.cs:9 S1");
    }

    private static Baseline Base(params (string Project, string Rule, int Count)[] entries)
    {
        var warnings = entries.GroupBy(e => e.Project).ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<string, int>)g.ToDictionary(e => e.Rule, e => e.Count));
        return new Baseline("c", "t", [], warnings);
    }

    [Fact]
    public void Tighten_lowers_counts_and_drops_zero_rules()
    {
        var change = BaselineEvolution.Tighten(Base(("P", "A", 5), ("P", "B", 2), ("P", "C", 1)), Base(("P", "A", 3), ("P", "C", 1)));

        change.Should().NotBeNull();
        change!.Entries.Should().Be(2);
        change.Baseline.Warnings["P"].Should().Equal(new Dictionary<string, int> { ["A"] = 3, ["C"] = 1 });
    }

    [Fact]
    public void Tighten_never_raises_and_returns_null_when_nothing_is_lower()
    {
        BaselineEvolution.Tighten(Base(("P", "A", 5)), Base(("P", "A", 7), ("P", "Z", 1))).Should().BeNull();
    }

    [Fact]
    public void Tighten_keeps_the_previous_stamp_and_coverage()
    {
        var previous = Base(("P", "A", 5)) with { Projects = [new ProjectMetrics("P", Layer.Core, 80)] };
        var current = new Baseline("new", "later", [], Base(("P", "A", 1)).Warnings);

        var tightened = BaselineEvolution.Tighten(previous, current)!.Baseline;

        tightened.Commit.Should().Be("c");
        tightened.MeasuredAt.Should().Be("t");
        tightened.Projects.Should().Equal(previous.Projects);
    }

    [Fact]
    public void Tighten_leaves_projects_missing_from_current_untouched()
    {
        var change = BaselineEvolution.Tighten(Base(("P", "A", 5), ("Q", "A", 4)), Base(("P", "A", 1)));

        change!.Baseline.Warnings["Q"]["A"].Should().Be(4);
        change.Baseline.Warnings["P"]["A"].Should().Be(1);
    }

    [Fact]
    public void AddMissing_adds_only_absent_pairs_and_keeps_existing_values()
    {
        var found = new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["P"] = new Dictionary<string, int> { ["A"] = 99, ["N"] = 3 },
            ["New"] = new Dictionary<string, int> { ["A"] = 1 }
        };

        var change = BaselineEvolution.AddMissing(Base(("P", "A", 5)), found);

        change.Entries.Should().Be(2);
        change.Baseline.Warnings["P"].Should().Equal(new Dictionary<string, int> { ["A"] = 5, ["N"] = 3 });
        change.Baseline.Warnings["New"]["A"].Should().Be(1);
    }

    private sealed class MergeFakes(Baseline? existing, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> found) : IBaselineStore, ISarifSource, IPrFiles
    {
        public List<(string Path, Baseline Baseline)> Saved = [];
        public bool HasFiles = true;

        public Baseline? Load(string path) => existing;

        public void Save(string path, Baseline baseline) => Saved.Add((path, baseline));

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Aggregate(IReadOnlyList<string> files) => found;

        public IReadOnlyList<Finding> Read(IReadOnlyList<string> files) => [];

        public IReadOnlyList<string> Expand(IEnumerable<string> patterns) => HasFiles ? ["a.sarif"] : [];

        public bool Exists(string path) => true;
    }

    [Fact]
    public void Merge_new_saves_baseline_with_added_pairs()
    {
        var fakes = new MergeFakes(Base(("P", "A", 5)), new Dictionary<string, IReadOnlyDictionary<string, int>> { ["P"] = new Dictionary<string, int> { ["B"] = 2 } });

        var result = new BaselineMergeRunner(fakes, fakes, fakes).MergeNew("/repo", ["s"]);

        result.Added.Should().Be(1);
        result.Path.Should().Be(Path.Combine("/repo", "quality", "baseline.json"));
        fakes.Saved.Should().ContainSingle().Which.Baseline.Warnings["P"].Should().Equal(new Dictionary<string, int> { ["A"] = 5, ["B"] = 2 });
    }

    [Fact]
    public void Merge_new_does_not_write_when_nothing_is_new()
    {
        var fakes = new MergeFakes(Base(("P", "A", 5)), new Dictionary<string, IReadOnlyDictionary<string, int>> { ["P"] = new Dictionary<string, int> { ["A"] = 9 } });

        new BaselineMergeRunner(fakes, fakes, fakes).MergeNew("/repo", ["s"]).Added.Should().Be(0);

        fakes.Saved.Should().BeEmpty();
    }

    [Fact]
    public void Merge_new_requires_baseline_and_sarif_files()
    {
        var empty = new Dictionary<string, IReadOnlyDictionary<string, int>>();

        var noBaseline = new MergeFakes(null, empty);
        new Action(() => new BaselineMergeRunner(noBaseline, noBaseline, noBaseline).MergeNew("/repo", ["s"])).Should().Throw<InvalidOperationException>();

        var noFiles = new MergeFakes(Base(("P", "A", 1)), empty) { HasFiles = false };
        new Action(() => new BaselineMergeRunner(noFiles, noFiles, noFiles).MergeNew("/repo", ["s"])).Should().Throw<InvalidOperationException>();
    }
}
