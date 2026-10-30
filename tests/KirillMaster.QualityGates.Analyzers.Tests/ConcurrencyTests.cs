namespace KirillMaster.QualityGates.Analyzers.Tests;

public sealed class ConcurrencyTests
{
    private static readonly (string Key, string Value) Core = ("build_property.QualityGatesLayer", "core");

    private static Task<string[]> Ids(string source, params (string Key, string Value)[] config) =>
        AnalyzerHarness.DiagnoseWithConfigAsync(source, config);

    [Fact]
    public async Task Lock_in_core_is_reported() =>
        Assert.Equal(["QG0188"], await Ids("class C { private readonly object _gate = new(); int _n; void M() { lock (_gate) { _n++; } } }", Core));

    [Fact]
    public async Task Lock_in_core_top_level_code_is_reported() =>
        Assert.Contains("QG0188", await Ids("var gate = new object(); lock (gate) { }", Core));

    [Fact]
    public async Task Lock_outside_core_is_allowed()
    {
        const string Source = "class C { private readonly object _gate = new(); void M() { lock (_gate) { } } }";

        Assert.Empty(await Ids(Source, ("build_property.QualityGatesLayer", "adapter")));
        Assert.Empty(await Ids(Source, ("build_property.QualityGatesLayer", "test")));
    }

    [Fact]
    public async Task Static_non_readonly_field_in_core_is_reported() =>
        Assert.Equal(["QG0189"], await Ids("class C { private static int _counter; }", Core));

    [Fact]
    public async Task Static_readonly_mutable_collection_in_core_is_reported() =>
        Assert.Equal(["QG0189"], await Ids("using System.Collections.Generic; class C { private static readonly Dictionary<string, int> Cache = new(); }", Core));

    [Fact]
    public async Task Concurrent_collection_in_core_is_reported() =>
        Assert.Equal(["QG0189"], await Ids("using System.Collections.Concurrent; class C { private static readonly ConcurrentDictionary<string, int> Cache = new(); }", Core));

    [Fact]
    public async Task Immutable_static_state_in_core_is_allowed() =>
        Assert.Empty(await Ids(
            "using System.Collections.Immutable; using System.Collections.Generic; enum E { A } class C { private const int Max = 3; private static readonly string Name = \"x\"; private static readonly ImmutableArray<int> Items = ImmutableArray.Create(1); private static readonly IReadOnlyList<int> Ro = new int[0]; private int _instance; }",
            Core));

    [Fact]
    public async Task Static_mutable_field_outside_core_is_allowed() =>
        Assert.Empty(await Ids("class C { private static int _counter; }", ("build_property.QualityGatesLayer", "adapter")));

    [Fact]
    public async Task Static_mutable_field_in_test_assembly_is_skipped() =>
        Assert.Empty(await AnalyzerHarness.DiagnoseInAssemblyAsync("class C { private static int _counter; void M() { lock (this) { } } }", "App.Tests", string.Empty, Core));

    [Fact]
    public async Task Top_level_statements_do_not_crash_the_design_analyzer()
    {
        var ids = await Ids("var flag = args.Length > 0; System.Console.WriteLine(flag); public partial class Program { }", Core);

        Assert.DoesNotContain("AD0001", ids);
    }
}
