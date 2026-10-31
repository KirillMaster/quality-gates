using System.Text.Json;
using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Pr;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Tests;

public sealed class PrAdapterHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qg-s7-" + Guid.NewGuid().ToString("N"));

    public PrAdapterHardeningTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Touch(string rel)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        return full;
    }

    private string Pat(string rel) => _root.Replace('\\', '/') + "/" + rel;

    private static readonly IReadOnlyDictionary<string, Layer> NoOverrides = new Dictionary<string, Layer>();

    [Fact]
    [Trait("scenario", "US2-AS47")]
    public void Glob_existing_file_is_returned_as_is_and_exists_reports_it()
    {
        var f = Touch("a/one.xml");
        var g = new GlobFiles();
        g.Expand([f]).Should().Equal(Path.GetFullPath(f));
        g.Exists(f).Should().BeTrue();
        g.Exists(f + ".nope").Should().BeFalse();
    }

    [Fact]
    [Trait("scenario", "US2-AS47")]
    public void Glob_directory_is_enumerated_recursively()
    {
        var a = Touch("d/a.xml");
        var b = Touch("d/sub/b.xml");
        new GlobFiles().Expand([Pat("d")]).Should().BeEquivalentTo([Path.GetFullPath(a), Path.GetFullPath(b)]);
    }

    [Fact]
    [Trait("scenario", "US2-AS52")]
    public void Glob_missing_literal_and_missing_base_dir_give_empty()
    {
        var g = new GlobFiles();
        g.Expand([Pat("none.xml")]).Should().BeEmpty();
        g.Expand([Pat("nodir/*.xml")]).Should().BeEmpty();
    }

    [Fact]
    [Trait("scenario", "US2-AS47")]
    public void Glob_star_matches_within_one_segment_only_and_escapes_dots()
    {
        Touch("c/a.xml");
        var top = Touch("c/b.xml");
        Touch("c/sub/deep.xml");
        Touch("c/bxxml");
        Touch("c/a.XML.bak");
        var r = new GlobFiles().Expand([Pat("c/*.xml")]);
        r.Should().Contain(Path.GetFullPath(top));
        r.Should().HaveCount(2);
        r.Should().NotContain(x => x.Contains("deep") || x.EndsWith("bxxml") || x.EndsWith(".bak"));
    }

    [Fact]
    [Trait("scenario", "US2-AS47")]
    public void Glob_question_mark_matches_exactly_one_non_separator_char()
    {
        var a = Touch("q/a1.xml");
        Touch("q/a12.xml");
        Touch("q/a.xml");
        new GlobFiles().Expand([Pat("q/a?.xml")]).Should().Equal(Path.GetFullPath(a));
    }

    [Fact]
    [Trait("scenario", "US2-AS47")]
    public void Glob_double_star_slash_matches_zero_or_more_directories()
    {
        var z = Touch("w/a.xml");
        var one = Touch("w/x/a.xml");
        var two = Touch("w/x/y/a.xml");
        Touch("w/x/y/b.xml");
        new GlobFiles().Expand([Pat("w/**/a.xml")]).Should().BeEquivalentTo(
            [Path.GetFullPath(z), Path.GetFullPath(one), Path.GetFullPath(two)]);
    }

    [Fact]
    [Trait("scenario", "US2-AS47")]
    public void Glob_double_star_matches_across_separators()
    {
        var a = Touch("s/x/y/a.xml");
        Touch("s/x/y/a.txt");
        new GlobFiles().Expand([Pat("s/**.xml")]).Should().Equal(Path.GetFullPath(a));
    }

    [Fact]
    [Trait("scenario", "US2-AS47")]
    public void Glob_is_case_insensitive_sorted_and_deduplicated_across_patterns()
    {
        var b = Touch("m/B.XML");
        var a = Touch("m/a.xml");
        var r = new GlobFiles().Expand([Pat("m/*.xml"), Pat("m/*.xml"), a]);
        r.Should().Equal(new[] { Path.GetFullPath(a), Path.GetFullPath(b) }.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    [Trait("scenario", "US2-AS47")]
    public void Glob_relative_pattern_without_slash_uses_current_directory()
    {
        var prev = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_root);
            var a = Touch("rel-only.xml");
            new GlobFiles().Expand(["rel-*.xml"]).Should().Equal(Path.GetFullPath(a));
        }
        finally
        {
            Directory.SetCurrentDirectory(prev);
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS48")]
    public void Locator_classifies_by_nearest_csproj_name_and_folder()
    {
        Touch("src/Sample.Domain/Sample.Domain.csproj");
        Touch("src/Sample.Domain/Calc.cs");
        Touch("src/Sample.Adapters/Sample.Adapters.csproj");
        Touch("src/Sample.Adapters/deep/er/Repo.cs");
        Touch("tests/Sample.Tests/Sample.Tests.csproj");
        Touch("tests/Sample.Tests/T.cs");
        Touch("src/Core/Thing/Thing.csproj");
        Touch("src/Core/Thing/X.cs");
        var l = new CsprojProjectLocator();

        l.Locate(_root, "src/Sample.Domain/Calc.cs", NoOverrides).Should().Be(new ProjectRef("Sample.Domain", Layer.Core));
        l.Locate(_root, "src/Sample.Adapters/deep/er/Repo.cs", NoOverrides).Should().Be(new ProjectRef("Sample.Adapters", Layer.Adapter));
        l.Locate(_root, "tests/Sample.Tests/T.cs", NoOverrides).Should().Be(new ProjectRef("Sample.Tests", Layer.Test));
        l.Locate(_root, "src/Core/Thing/X.cs", NoOverrides).Should().Be(new ProjectRef("Thing", Layer.Core));
    }

    [Fact]
    [Trait("scenario", "US2-AS48")]
    public void Locator_override_wins_over_naming_convention()
    {
        Touch("src/Sample.Domain/Sample.Domain.csproj");
        Touch("src/Sample.Domain/Calc.cs");
        new CsprojProjectLocator()
            .Locate(_root, "src/Sample.Domain/Calc.cs", new Dictionary<string, Layer> { ["Sample.Domain"] = Layer.Adapter })
            .Should().Be(new ProjectRef("Sample.Domain", Layer.Adapter));
    }

    [Fact]
    [Trait("scenario", "US2-AS48")]
    public void Locator_picks_first_csproj_in_ordinal_order_and_works_for_missing_directory()
    {
        Touch("p/Zeta.Domain.csproj");
        Touch("p/Alpha.Adapters.csproj");
        Touch("p/F.cs");
        var l = new CsprojProjectLocator();
        l.Locate(_root, "p/F.cs", NoOverrides)!.Name.Should().Be("Alpha.Adapters");
        l.Locate(_root, "p/not/yet/there/G.cs", NoOverrides)!.Name.Should().Be("Alpha.Adapters");
    }

    [Fact]
    [Trait("scenario", "US2-AS48")]
    public void Locator_returns_null_without_csproj_and_does_not_climb_above_repo_root()
    {
        Touch("outer/Outer.Domain.csproj");
        var repo = Path.Combine(_root, "outer", "repo");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, "src", "A.cs"), "x");
        new CsprojProjectLocator().Locate(repo, "src/A.cs", NoOverrides).Should().BeNull();
    }

    [Fact]
    [Trait("scenario", "US2-AS48")]
    public void Locator_finds_csproj_located_at_repo_root()
    {
        Touch("Root.Domain.csproj");
        Touch("A.cs");
        new CsprojProjectLocator().Locate(_root, "A.cs", NoOverrides).Should().Be(new ProjectRef("Root.Domain", Layer.Core));
    }

    [Fact]
    [Trait("scenario", "US2-AS56")]
    public void ReportStore_writes_indented_camelcase_kebab_enums_and_omits_nulls()
    {
        var dir = Path.Combine(_root, "n", "out");
        var report = new RunReport(
            RunMode.Pr, Policy.Blocking,
            [new GateResult(GateId.DiffCoverage, Verdict.NotApplicable, ResultLayer.Core, 50.5, 90, ["a.cs:1"], null)],
            2, "feature/x", "origin/develop", "alice", ["static"], null);

        new JsonReportStore().Write(dir, report);

        var path = Path.Combine(dir, JsonReportStore.FileName);
        var text = File.ReadAllText(path);
        text.Should().Contain("\n");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        root.GetProperty("mode").GetString().Should().Be("pr");
        root.GetProperty("policy").GetString().Should().Be("blocking");
        root.GetProperty("sourceBranch").GetString().Should().Be("feature/x");
        root.GetProperty("targetBranch").GetString().Should().Be("origin/develop");
        root.GetProperty("exitCode").GetInt32().Should().Be(2);
        root.GetProperty("bypassedBy").GetString().Should().Be("alice");
        root.GetProperty("skipped")[0].GetString().Should().Be("static");
        root.TryGetProperty("error", out _).Should().BeFalse();
        var r0 = root.GetProperty("results")[0];
        r0.GetProperty("verdict").GetString().Should().Be("not-applicable");
        r0.GetProperty("layer").GetString().Should().Be("core");
        r0.GetProperty("actual").GetDouble().Should().Be(50.5);
        r0.GetProperty("threshold").GetDouble().Should().Be(90);
        r0.GetProperty("offenders")[0].GetString().Should().Be("a.cs:1");
        r0.TryGetProperty("message", out _).Should().BeFalse();
    }

    [Fact]
    [Trait("scenario", "US2-AS52")]
    public void ReportStore_error_report_carries_error_text()
    {
        var dir = Path.Combine(_root, "e");
        new JsonReportStore().Write(dir, new RunReport(RunMode.Pr, Policy.Blocking, [], 3, "s", "t", null, [], "boom"));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "quality-report.json")));
        doc.RootElement.GetProperty("error").GetString().Should().Be("boom");
        doc.RootElement.GetProperty("exitCode").GetInt32().Should().Be(3);
        doc.RootElement.TryGetProperty("bypassedBy", out _).Should().BeFalse();
    }
}
