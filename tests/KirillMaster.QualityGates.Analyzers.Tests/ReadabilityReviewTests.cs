using Microsoft.CodeAnalysis;

namespace KirillMaster.QualityGates.Analyzers.Tests;

public sealed class ReadabilityReviewTests
{
    private const string Layer = "build_property.QualityGatesLayer";

    private const string Mixed =
        "namespace Sample.Core { public class Svc { void A() { } void B() { } void C() { }\n"
        + "public int Run(int[] xs) { A(); B(); C(); var t = 0; foreach (var x in xs) { t += x; } foreach (var x in xs) { t += x; } foreach (var x in xs) { t += x; } return t; } } }";

    private static Task<string[]> Run(string source, params (string, string)[] config) =>
        ReadabilityHarness.DiagnoseAsync(source, "Sample", null, config);

    [Fact]
    public async Task M1_adapter_layer_is_never_core_even_with_core_namespace() =>
        Assert.DoesNotContain("QG0184", await Run(Mixed, (Layer, "adapter")));

    [Fact]
    public async Task M1_core_layer_applies_to_any_namespace() =>
        Assert.Contains("QG0184", await Run(Mixed.Replace("Sample.Core", "Sample.Adapters", StringComparison.Ordinal), (Layer, "core")));

    [Fact]
    public async Task M1_namespace_heuristic_applies_only_without_layer_and_skips_test_namespaces()
    {
        Assert.Contains("QG0184", await Run(Mixed));
        Assert.DoesNotContain("QG0184", await Run(Mixed.Replace("Sample.Core", "Sample.Core.UnitTests", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task M1_test_compilation_is_skipped() =>
        Assert.DoesNotContain("QG0184", await Run(Mixed + "\npublic class T { [Xunit.Fact] public void A_b_c() { } }"));

    [Fact]
    public async Task M4_serialization_attributes_records_and_allowlisted_stems_are_exempt()
    {
        var ids = await Run(
            "public record Dto(int Line1, bool Active);\n"
            + "public class Row { [System.Text.Json.Serialization.JsonPropertyName(\"a\")] public int Addr1 { get; set; } public int AddressLine1; public int Phone2; }");

        Assert.DoesNotContain("QG0181", ids);
        Assert.DoesNotContain("QG0180", ids);
    }

    [Fact]
    public async Task M4_plain_behavioral_class_is_still_checked()
    {
        var ids = await Run("class Svc { public bool Active; public int total2; public void Work() { } }");

        Assert.Contains("QG0180", ids);
        Assert.Contains("QG0181", ids);
    }

    [Theory]
    [InlineData("// Fixed by design, see ADR")]
    [InlineData("// jd 2019-03-04 is the cutoff")]
    [InlineData("// Added by the retry policy")]
    public async Task M5_ordinary_phrases_are_not_bylines(string comment) =>
        Assert.DoesNotContain("QG0176", await Run(comment + "\nclass C { }"));

    [Theory]
    [InlineData("// FIXME later", true)]
    [InlineData("// FIXME ABC-123 later", false)]
    [InlineData("// HACK see #42", false)]
    [InlineData("// temporary file cleanup", false)]
    [InlineData("// temporary directory is wiped", false)]
    [InlineData("// untemporary", false)]
    [InlineData("// temporary until the API ships", true)]
    public async Task L6_markers_respect_keys_and_word_boundaries(string comment, bool reported) =>
        Assert.Equal(reported, (await Run(comment + "\nclass C { }")).Contains("QG0179"));

    [Fact]
    public async Task L6_obsolete_needs_a_ticket()
    {
        Assert.Contains("QG0179", await Run("class C { [System.Obsolete] void A() { } }"));
        Assert.Contains("QG0179", await Run("class C { [System.Obsolete(\"use B\")] void A() { } }"));
        Assert.DoesNotContain("QG0179", await Run("class C { [System.Obsolete(\"use B, see ABC-12\")] void A() { } }"));
    }

    [Fact]
    public async Task L6_todo_with_jira_key_is_accepted_by_qg0013()
    {
        Assert.Contains("QG0013", await Run("// TODO tidy\nclass C { }"));
        Assert.DoesNotContain("QG0013", await Run("// TODO ABC-12 tidy\nclass C { }"));
    }

    [Fact]
    public async Task L7_type_lie_only_for_list_and_dictionary_on_system_types()
    {
        var ids = await Run("class ProductList { } class C { ProductList productList = new(); int[] valueArray = []; string accountList = \"\"; string nameDictionary = \"\"; }");

        Assert.Equal(2, ids.Count(i => i == "QG0183"));
    }

    [Fact]
    public async Task L11_record_struct_is_checked_for_stepdown() =>
        Assert.Contains("QG0186", await Run("record struct R(int X) { void Helper() { } public void Run() { Helper(); } }"));

    [Fact]
    public void L10_refactor_globalconfig_covers_every_descriptor_category()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "KirillMaster.QualityGates.Build")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var config = File.ReadAllText(Path.Combine(dir!.FullName, "src", "KirillMaster.QualityGates.Build", "buildTransitive", "quality.refactor.globalconfig"));
        var categories = ReadabilityHarness.Descriptors().Select(d => d.Category).Distinct().ToArray();

        Assert.All(categories, c => Assert.Contains("category-" + c + ".severity = error", config, StringComparison.Ordinal));
    }
}
