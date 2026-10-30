namespace KirillMaster.QualityGates.Analyzers.Tests;

public sealed class ClosedSwitchTests
{
    private const string Shapes = "abstract class Shape { private protected Shape() { } } sealed class Circle : Shape { } sealed class Square : Shape { } ";

    private static Task<string[]> Ids(string source) => AnalyzerHarness.DiagnoseWithConfigAsync(source);

    [Fact]
    public async Task Discard_arm_on_sealed_hierarchy_is_reported() =>
        Assert.Equal(["QG0190"], await Ids(Shapes + "class C { int M(Shape s) => s switch { Circle => 1, _ => 2 }; }"));

    [Fact]
    public async Task Exhaustive_sealed_hierarchy_is_allowed() =>
        Assert.Empty(await Ids(Shapes + "class C { int M(Shape s) => s switch { Circle => 1, Square => 2 }; }"));

    [Fact]
    public async Task Throwing_discard_on_closed_type_is_allowed() =>
        Assert.DoesNotContain("QG0190", await Ids("using System.Diagnostics; " + Shapes + "class C { int M(Shape s) => s switch { Circle => 1, Square => 2, _ => throw new UnreachableException() }; }"));

    [Fact]
    public async Task Default_section_on_sealed_hierarchy_is_reported() =>
        Assert.Contains("QG0190", await Ids(Shapes + "class C { int M(Shape s) { switch (s) { case Circle: return 1; default: return 2; } } }"));

    [Fact]
    public async Task Throwing_default_section_is_allowed() =>
        Assert.DoesNotContain("QG0190", await Ids(Shapes + "class C { int M(Shape s) { switch (s) { case Circle: return 1; case Square: return 2; default: throw new System.InvalidOperationException(); } } }"));

    [Fact]
    public async Task Discard_arm_on_bool_is_reported() =>
        Assert.Equal(["QG0190"], await Ids("class C { int M(bool b) => b switch { true => 1, _ => 2 }; }"));

    [Fact]
    public async Task Discard_arm_on_bool_tuple_is_reported() =>
        Assert.Equal(["QG0190"], await Ids("class C { int M(bool a, bool b) => (a, b) switch { (true, true) => 1, _ => 2 }; }"));

    [Fact]
    public async Task Exhaustive_bool_tuple_is_allowed() =>
        Assert.Empty(await Ids("class C { int M(bool a, bool b) => (a, b) switch { (true, _) => 1, (false, true) => 2, (false, false) => 3 }; }"));

    [Fact]
    public async Task Enum_switch_is_left_to_qg0022() =>
        Assert.Equal(["QG0022"], await Ids("enum E { A, B } class C { int M(E e) => e switch { E.A => 1, _ => 2 }; }"));

    [Fact]
    public async Task Open_hierarchy_is_allowed() =>
        Assert.Empty(await Ids("public abstract class Shape { } public sealed class Circle : Shape { } public class C { public int M(Shape s) => s switch { Circle => 1, _ => 2 }; }"));

    [Fact]
    public async Task Unsealed_derived_type_keeps_hierarchy_open() =>
        Assert.Empty(await Ids("abstract class Shape { private protected Shape() { } } class Circle : Shape { } class C { int M(Shape s) => s switch { Circle => 1, _ => 2 }; }"));

    [Fact]
    public async Task Open_type_discard_is_allowed() =>
        Assert.Empty(await Ids("class C { int M(int n, object o) => (n switch { 1 => 1, _ => 2 }) + (o switch { string => 1, _ => 2 }); }"));

    [Fact]
    public async Task Guarded_discard_is_not_a_catch_all() =>
        Assert.Empty(await Ids("class C { int M(bool b, int n) => b switch { true => 1, false => 2, _ when n > 0 => 3 }; }"));

    [Fact]
    public async Task Top_level_statements_do_not_crash()
    {
        var ids = await Ids("var flag = args.Length > 0; var n = flag switch { true => 1, _ => 2 }; System.Console.WriteLine(n); public partial class Program { }");

        Assert.DoesNotContain("AD0001", ids);
        Assert.Contains("QG0190", ids);
    }
}
