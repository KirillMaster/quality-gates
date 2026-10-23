namespace KirillMaster.QualityGates.Analyzers.Tests;

public sealed class AnalyzerTests
{
    private static string Lines(int count) => string.Concat(Enumerable.Repeat("        var x = 1;\n", count));

    [Fact]
    public void Every_descriptor_has_unique_qg_id_and_help_link()
    {
        var descriptors = AnalyzerHarness.Descriptors().DistinctBy(d => d.Id).ToArray();

        Assert.All(descriptors, d => Assert.Matches("^QG0\\d{3}$", d.Id));
        Assert.All(descriptors, d => Assert.EndsWith(d.Id.ToLowerInvariant(), d.HelpLinkUri, StringComparison.Ordinal));
        Assert.Equal(22, descriptors.Length);
    }

    [Fact]
    public async Task Clean_code_has_no_diagnostics() =>
        Assert.Empty(await AnalyzerHarness.DiagnoseAsync("class C { int M(int a) => a + 1; }"));

    [Fact]
    public async Task Long_method_is_reported()
    {
        var source = "class C { void M() {\n" + Lines(70) + "} }";

        Assert.Contains("QG0002", await AnalyzerHarness.DiagnoseAsync(source));
    }

    [Fact]
    public async Task Long_type_is_reported()
    {
        var source = "class C {\n" + string.Concat(Enumerable.Range(0, 320).Select(i => $"    int F{i};\n")) + "}";

        Assert.Contains("QG0001", await AnalyzerHarness.DiagnoseAsync(source));
    }

    [Fact]
    public async Task Complex_method_is_reported()
    {
        var branches = string.Concat(Enumerable.Range(0, 11).Select(i => $"if (a == {i}) {{ return {i}; }}\n"));

        Assert.Contains("QG0003", await AnalyzerHarness.DiagnoseAsync($"class C {{ int M(int a) {{ {branches} return 0; }} }}"));
    }

    [Fact]
    public async Task Deep_nesting_is_reported() =>
        Assert.Contains("QG0004", await AnalyzerHarness.DiagnoseAsync(
            "class C { void M(int a) { if (a > 0) { while (a > 1) { for (;;) { foreach (var i in new[] { 1 }) { a++; } } } } } }"));

    [Fact]
    public async Task Parameter_and_dependency_limits_are_reported()
    {
        var ids = await AnalyzerHarness.DiagnoseAsync("class C { public C(int a, int b, int c, int d, int e, int f, int g, int h) { } void M(int a, int b, int c, int d, int e, int f) { } }");

        Assert.Contains("QG0005", ids);
        Assert.Contains("QG0006", ids);
    }

    [Fact]
    public async Task Empty_catch_and_rethrow_are_reported()
    {
        var ids = await AnalyzerHarness.DiagnoseAsync("using System; class C { void M() { try { } catch (Exception) { } try { } catch (Exception ex) { throw ex; } } }");

        Assert.Contains("QG0010", ids);
        Assert.Contains("QG0011", ids);
    }

    [Fact]
    public async Task Interpolated_log_is_reported() =>
        Assert.Contains("QG0012", await AnalyzerHarness.DiagnoseAsync("class L { public void LogInformation(string m) { } } class C { void M(L l, int a) { l.LogInformation($\"v {a}\"); } }"));

    [Fact]
    public async Task Todo_without_issue_and_banner_are_reported()
    {
        var ids = await AnalyzerHarness.DiagnoseAsync("// TODO fix\n// ------------\nclass C { }");

        Assert.Contains("QG0013", ids);
        Assert.Contains("QG0014", ids);
    }

    [Fact]
    public async Task Todo_with_issue_is_accepted() =>
        Assert.DoesNotContain("QG0013", await AnalyzerHarness.DiagnoseAsync("// TODO #12 fix\nclass C { }"));

    [Fact]
    public async Task Swallowed_exception_and_null_from_catch_are_reported()
    {
        var ids = await AnalyzerHarness.DiagnoseAsync("using System; class C { string? M() { try { return \"a\"; } catch (Exception) { var x = 1; return null; } } }");

        Assert.Contains("QG0020", ids);
        Assert.Contains("QG0021", ids);
    }

    [Fact]
    public async Task Logged_catch_is_not_swallowed() =>
        Assert.DoesNotContain("QG0020", await AnalyzerHarness.DiagnoseAsync("using System; class L { public void LogError(string m) { } } class C { void M(L l) { try { } catch (Exception) { l.LogError(\"x\"); } } }"));

    [Fact]
    public async Task Enum_switch_without_throwing_default_is_reported() =>
        Assert.Contains("QG0022", await AnalyzerHarness.DiagnoseAsync("enum E { A, B } class C { int M(E e) { switch (e) { case E.A: return 1; default: return 2; } } }"));

    [Fact]
    public async Task Enum_switch_with_throwing_default_is_accepted() =>
        Assert.DoesNotContain("QG0022", await AnalyzerHarness.DiagnoseAsync("using System; enum E { A, B } class C { int M(E e) => e switch { E.A => 1, E.B => 2, _ => throw new ArgumentOutOfRangeException(nameof(e)) }; }"));

    [Fact]
    public async Task Enum_to_enum_cast_is_reported() =>
        Assert.Contains("QG0023", await AnalyzerHarness.DiagnoseAsync("enum A { X } enum B { X } class C { B M(A a) => (B)a; }"));

    [Fact]
    public async Task Async_void_is_reported_but_event_handlers_are_not()
    {
        var ids = await AnalyzerHarness.DiagnoseAsync("using System; using System.Threading.Tasks; class C { async void M() { await Task.Yield(); } async void H(object s, EventArgs e) { await Task.Yield(); } }");

        Assert.Single(ids, "QG0024");
    }

    [Fact]
    public async Task Null_collection_is_reported_but_nullable_is_accepted()
    {
        var bad = await AnalyzerHarness.DiagnoseAsync("#nullable disable\nusing System.Collections.Generic; class C { List<int> M() { return null; } }");
        var good = await AnalyzerHarness.DiagnoseAsync("using System.Collections.Generic; class C { List<int>? M() { return null; } }");

        Assert.Contains("QG0030", bad);
        Assert.DoesNotContain("QG0030", good);
    }

    [Fact]
    public async Task Opt_in_design_rules_fire_only_when_enabled()
    {
        const string Source = "class C { public void M(bool flag, out int x) { x = 0; } }";

        Assert.Empty(await AnalyzerHarness.DiagnoseAsync(Source));
        var ids = await AnalyzerHarness.DiagnoseAsync(Source, "QG0031", "QG0032");
        Assert.Contains("QG0031", ids);
        Assert.Contains("QG0032", ids);
    }

    [Fact]
    public async Task Test_rules_apply_in_test_compilations()
    {
        const string Source = "using System; using Xunit; public class T { [Fact(Skip = \"later\")] public void A() { var n = DateTime.Now; } "
            + "[Fact] public void B() { Assert.True(true); Assert.True(true); Assert.True(true); Assert.True(true); Assert.True(true); Assert.True(true); } }";

        var ids = await AnalyzerHarness.DiagnoseAsync(Source);

        Assert.Contains("QG0040", ids);
        Assert.Contains("QG0041", ids);
        Assert.Contains("QG0042", ids);
    }

    [Fact]
    public async Task Skip_with_issue_is_accepted() =>
        Assert.DoesNotContain("QG0040", await AnalyzerHarness.DiagnoseAsync("using Xunit; public class T { [Fact(Skip = \"see #42\")] public void A() { } }"));
}
