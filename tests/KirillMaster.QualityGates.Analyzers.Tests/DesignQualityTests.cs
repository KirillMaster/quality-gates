namespace KirillMaster.QualityGates.Analyzers.Tests;

public sealed class DesignQualityTests
{
    private const string Repo = "public class Repo { public void Delete(int id) { } public void Save(int id) { } public void Load(int id) { } public void Find(int id) { } }\n";

    [Theory]
    [InlineData("int F(int max){int total=0; for(int i=1;i<=max;++i){ for(int j=2;j<i;++j){ if(i%j==0){ goto Next; } } total+=i; Next:; } return total;}", 7)]
    [InlineData("string F(int n){ switch(n){case 1: return \"a\"; case 2: return \"b\"; case 3: return \"c\"; default: return \"d\";} }", 1)]
    [InlineData("void F(bool c1,bool c2){ try{ if(c1){ for(int i=0;i<10;i++){ while(c2){} } } } catch(System.Exception e){ if(c2){} } }", 9)]
    [InlineData("int F(int x){ if(x==1) return 1; else if(x==2) return 2; else return 3; }", 3)]
    [InlineData("bool F(bool a,bool b,bool c,bool d,bool e,bool f){ if(a&&b&&c||d||e&&f) return true; return false; }", 4)]
    [InlineData("int F(int n){ if(n<=1) return 1; return n*F(n-1);}", 2)]
    [InlineData("void F(System.Collections.Generic.List<int> l){ l.ForEach(x=>{ if(x>0){} }); }", 2)]
    [InlineData("string? F(string? s,string? t){ return s?.Trim() ?? t?.Trim() ?? \"\"; }", 0)]
    [InlineData("int F(bool a,bool b){ return a ? (b ? 1 : 2) : 3; }", 3)]
    [InlineData("int F(int x,bool a){ if(a){ switch(x){case 1: return 1; case 2: return 2; default: return 0;} } return -1; }", 3)]
    [InlineData("int F(int x){ if(x==1) return 1; if(x==2) return 2; if(x==3) return 3; if(x==4) return 4; if(x==5) return 5; return 0; }", 5)]
    [InlineData("int F(bool a,bool b,bool c,bool d,bool e){ if(a){ if(b){ if(c){ if(d){ if(e){ return 1; } } } } } return 0; }", 15)]
    public async Task Cognitive_complexity_matches_reference_scores(string method, int expected)
    {
        var source = "class C { " + method + " }";

        var below = await AnalyzerHarness.DiagnoseWithConfigAsync(source, ("qg_max_cognitive_complexity", (expected - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var at = await AnalyzerHarness.DiagnoseWithConfigAsync(source, ("qg_max_cognitive_complexity", expected.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        if (expected > 0)
        {
            Assert.Contains("QG0007", below);
        }

        Assert.DoesNotContain("QG0007", at);
    }

    [Fact]
    public async Task Cognitive_complexity_default_is_fifteen()
    {
        const string Deep = "int F(bool a,bool b,bool c,bool d,bool e,bool f){ if(a){ if(b){ if(c){ if(d){ if(e){ if(f){ return 1; } } } } } } return 0; }";
        const string Shallow = "int F(bool a,bool b,bool c,bool d,bool e){ if(a){ if(b){ if(c){ if(d){ if(e){ return 1; } } } } } return 0; }";

        Assert.Contains("QG0007", await AnalyzerHarness.DiagnoseAsync("class C { " + Deep + " }"));
        Assert.DoesNotContain("QG0007", await AnalyzerHarness.DiagnoseAsync("class C { " + Shallow + " }"));
    }

    [Fact]
    public async Task Pass_through_method_is_reported() =>
        Assert.Contains("QG0033", await AnalyzerHarness.DiagnoseAsync(Repo + "public class Svc(Repo repo) { public void Delete(int id) => repo.Delete(id); }"));

    [Fact]
    public async Task Pass_through_via_field_with_await_is_reported() =>
        Assert.Contains("QG0033", await AnalyzerHarness.DiagnoseAsync(
            "using System.Threading.Tasks; public class Repo { public Task DeleteAsync(int id) => Task.CompletedTask; }\n"
            + "public class Svc { private readonly Repo _repo = new(); public async Task DeleteAsync(int id) { await _repo.DeleteAsync(id).ConfigureAwait(false); } }"));

    [Fact]
    public async Task Decorator_over_same_contract_is_not_pass_through() =>
        Assert.DoesNotContain("QG0033", await AnalyzerHarness.DiagnoseAsync(
            "public interface IRepo { void Delete(int id); }\npublic class Decorator(IRepo inner) : IRepo { public void Delete(int id) => inner.Delete(id); }"));

    [Fact]
    public async Task Forwarding_to_external_type_is_not_pass_through() =>
        Assert.DoesNotContain("QG0033", await AnalyzerHarness.DiagnoseAsync(
            "public class C(System.Text.StringBuilder sb) { public void Add(string s) => sb.Append(s); }"));

    [Fact]
    public async Task Forwarding_with_logic_is_not_pass_through()
    {
        var ids = await AnalyzerHarness.DiagnoseAsync(Repo
            + "public class Svc(Repo repo) { public void Delete(int id) { if (id > 0) { repo.Delete(id); } } public void Save(int id) => repo.Save(id + 1); }");

        Assert.DoesNotContain("QG0033", ids);
    }

    [Fact]
    public async Task Pass_through_in_composition_root_or_exempt_folder_is_accepted()
    {
        const string Source = Repo + "public class Svc(Repo repo) { public void Delete(int id) => repo.Delete(id); }";

        Assert.DoesNotContain("QG0033", await AnalyzerHarness.DiagnoseInAssemblyAsync(Source, "App.CompositionRoot"));
        Assert.DoesNotContain("QG0033", await AnalyzerHarness.DiagnoseInAssemblyAsync(Source, "App", "src/Controllers/Svc.cs"));
        Assert.Contains("QG0033", await AnalyzerHarness.DiagnoseInAssemblyAsync(Source, "App", "src/Services/Svc.cs"));
    }

    [Fact]
    public async Task Shallow_class_is_reported()
    {
        var ids = await AnalyzerHarness.DiagnoseAsync(Repo
            + "public class Facade(Repo r) { public void A(int id) => r.Delete(id); public void B(int id) => r.Save(id); public void C(int id) => r.Load(id); public void D(int id) => r.Find(id); }");

        Assert.Single(ids, "QG0034");
    }

    [Fact]
    public async Task Class_with_real_logic_is_not_shallow()
    {
        var ids = await AnalyzerHarness.DiagnoseAsync(Repo
            + "public class Facade(Repo r) { public void A(int id) => r.Delete(id); public int B(int id) { var x = id * 2; return x; } public int C(int id) { var x = id * 3; return x; } public int D(int id) { var x = id * 4; return x; } }");

        Assert.DoesNotContain("QG0034", ids);
    }

    [Fact]
    public async Task Shallow_class_respects_minimum_method_count() =>
        Assert.DoesNotContain("QG0034", await AnalyzerHarness.DiagnoseWithConfigAsync(
            Repo + "public class Facade(Repo r) { public void A(int id) => r.Delete(id); public void B(int id) => r.Save(id); public void C(int id) => r.Load(id); }",
            ("qg_min_shallow_methods", "4")));

    [Fact]
    public async Task Partial_class_is_reported() =>
        Assert.Contains("QG0035", await AnalyzerHarness.DiagnoseAsync("public partial class P { } public partial class P { }"));

    [Fact]
    public async Task Partial_class_required_by_source_generator_is_accepted()
    {
        var ids = await AnalyzerHarness.DiagnoseAsync(
            "public partial class R { [System.Text.RegularExpressions.GeneratedRegex(\"a\")] private static partial System.Text.RegularExpressions.Regex A(); }\n"
            + "[System.CodeDom.Compiler.GeneratedCode(\"t\", \"1\")] public partial class G { }");

        Assert.DoesNotContain("QG0035", ids);
    }

    [Fact]
    public async Task Partial_class_in_generated_file_is_ignored() =>
        Assert.DoesNotContain("QG0035", await AnalyzerHarness.DiagnoseInAssemblyAsync("public partial class P { }", "Sample", "Form.g.cs"));

    [Fact]
    public async Task Method_fan_out_is_reported_above_threshold()
    {
        var calls = string.Concat(Enumerable.Range(0, 11).Select(i => $"Other.M{i}(); "));

        Assert.Contains("QG0008", await AnalyzerHarness.DiagnoseAsync(Other(11) + $"public class C {{ public void Run() {{ {calls} }} }}"));
    }

    [Fact]
    public async Task Method_fan_out_at_threshold_and_framework_calls_are_accepted()
    {
        var calls = string.Concat(Enumerable.Range(0, 10).Select(i => $"Other.M{i}(); "));
        var strings = "var s = \"a\"; s.Trim(); s.ToUpper(); s.ToLower(); s.Split(','); s.Substring(0); s.Replace(\"a\", \"b\"); s.PadLeft(1); s.PadRight(1); s.Remove(0); s.Insert(0, \"x\"); s.IndexOf('a'); ";

        Assert.DoesNotContain("QG0008", await AnalyzerHarness.DiagnoseAsync(Other(10) + $"public class C {{ public void Run() {{ {calls} {strings} }} }}"));
    }

    [Fact]
    public async Task Method_fan_out_threshold_is_configurable()
    {
        const string Body = "Other.M0(); Other.M1(); Other.M2();";

        Assert.Contains("QG0008", await AnalyzerHarness.DiagnoseWithConfigAsync(Other(3) + $"public class C {{ public void Run() {{ {Body} }} }}", ("qg_max_method_fan_out", "2")));
    }

    [Fact]
    public async Task Service_locator_is_reported() =>
        Assert.Contains("QG0036", await AnalyzerHarness.DiagnoseAsync("using System; class C { object? M(IServiceProvider p) => p.GetService(typeof(C)); }"));

    [Fact]
    public async Task Service_locator_extension_methods_are_reported() =>
        Assert.Contains("QG0036", await AnalyzerHarness.DiagnoseAsync(
            "namespace Microsoft.Extensions.DependencyInjection { public static class ServiceProviderServiceExtensions { public static T GetRequiredService<T>(this System.IServiceProvider p) where T : notnull => (T)p.GetService(typeof(T))!; } }\n"
            + "class C { object M(System.IServiceProvider p) => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<C>(p); }"));

    [Fact]
    public async Task Service_locator_in_composition_root_is_accepted()
    {
        const string Source = "using System; class C { object? M(IServiceProvider p) => p.GetService(typeof(C)); }";

        Assert.DoesNotContain("QG0036", await AnalyzerHarness.DiagnoseInAssemblyAsync(Source, "App.CompositionRoot"));
        Assert.DoesNotContain("QG0036", await AnalyzerHarness.DiagnoseInAssemblyAsync(Source, "App", "Program.cs"));
        Assert.DoesNotContain("QG0036", await AnalyzerHarness.DiagnoseInAssemblyAsync("using System; IServiceProvider p = null!; p.GetService(typeof(int));", "App"));
    }

    [Fact]
    public async Task Composition_root_suffixes_are_configurable()
    {
        const string Source = "using System; class C { object? M(IServiceProvider p) => p.GetService(typeof(C)); }";

        Assert.DoesNotContain("QG0036", await AnalyzerHarness.DiagnoseInAssemblyAsync(Source, "App.Wiring", string.Empty, ("qg_composition_root_suffixes", ".Wiring")));
        Assert.Contains("QG0036", await AnalyzerHarness.DiagnoseInAssemblyAsync(Source, "App.CompositionRoot", string.Empty, ("qg_composition_root_suffixes", ".Wiring")));
    }

    [Theory]
    [InlineData("class OrderHelper { }")]
    [InlineData("interface IStringUtils { }")]
    [InlineData("record PaymentManager(int Id);")]
    [InlineData("class DataImpl { }")]
    public async Task Banned_name_suffix_is_reported(string source) =>
        Assert.Contains("QG0050", await AnalyzerHarness.DiagnoseAsync(source));

    [Theory]
    [InlineData("class ExperienceManagerContext { }")]
    [InlineData("class Helpful { }")]
    [InlineData("class OrderService { }")]
    public async Task Allowed_type_names_are_accepted(string source) =>
        Assert.DoesNotContain("QG0050", await AnalyzerHarness.DiagnoseAsync(source));

    [Fact]
    public async Task Banned_name_suffixes_are_configurable()
    {
        var ids = await AnalyzerHarness.DiagnoseWithConfigAsync("class OrderService { } class OrderHelper { }", ("qg_banned_name_suffixes", "Service"));

        Assert.Single(ids, "QG0050");
    }

    [Fact]
    public async Task Abbreviation_tokens_are_reported_in_parameters_locals_and_members()
    {
        var ids = await AnalyzerHarness.DiagnoseAsync("class C { int cfgValue; void M(int svc) { var tmp = 1; foreach (var evt in new[] { 1 }) { } } }");

        Assert.Equal(4, ids.Count(id => id == "QG0051"));
    }

    [Fact]
    public async Task Common_short_names_and_overrides_are_accepted()
    {
        var ids = await AnalyzerHarness.DiagnoseAsync("using System.Threading; class C { void M(CancellationToken ct, int id, System.Exception ex) { } public override bool Equals(object? obj) => false; public override int GetHashCode() => 0; }");

        Assert.DoesNotContain("QG0051", ids);
    }

    [Fact]
    public async Task Abbreviation_tokens_are_configurable()
    {
        const string Source = "class C { void M(int req, int fooBar) { } }";

        Assert.Single(await AnalyzerHarness.DiagnoseWithConfigAsync(Source, ("qg_abbreviation_tokens", "foo")), "QG0051");
        Assert.Single(await AnalyzerHarness.DiagnoseAsync(Source), "QG0051");
    }

    [Fact]
    public async Task Recursion_is_resolved_semantically()
    {
        const string Source = "class C { int F(int n) { return n; } int G(int n) { if (n <= 1) return 1; return n * F(n - 1) + new D().G(n); } } class D { public int G(int n) => n; }";

        Assert.DoesNotContain("QG0007", await AnalyzerHarness.DiagnoseWithConfigAsync(Source, ("qg_max_cognitive_complexity", "1")));
        Assert.Contains("QG0007", await AnalyzerHarness.DiagnoseWithConfigAsync(Source, ("qg_max_cognitive_complexity", "0")));
    }

    [Fact]
    public async Task Partial_program_class_and_exempt_locations_are_accepted()
    {
        Assert.DoesNotContain("QG0035", await AnalyzerHarness.DiagnoseAsync("public partial class Program { }"));
        Assert.DoesNotContain("QG0035", await AnalyzerHarness.DiagnoseInAssemblyAsync("public partial class P { }", "App.CompositionRoot"));
        Assert.DoesNotContain("QG0035", await AnalyzerHarness.DiagnoseInAssemblyAsync("public partial class P { }", "App", "src/Controllers/P.cs"));
    }

    [Fact]
    public async Task Partial_class_with_generator_attribute_on_another_part_is_accepted() =>
        Assert.DoesNotContain("QG0035", await AnalyzerHarness.DiagnoseAsync(
            "public partial class Vm { [ObservableProperty] private int _count; } public partial class Vm { }\n"
            + "[System.AttributeUsage(System.AttributeTargets.Field)] public sealed class ObservablePropertyAttribute : System.Attribute { }"));

    [Fact]
    public async Task Pass_through_via_property_receiver_is_reported_and_zero_argument_forwarding_is_not()
    {
        const string Source = "public class Repo { public void Delete(int id) { } public void Clear() { } }\n"
            + "public class Svc { private Repo Repo { get; } = new(); public void Delete(int id) => Repo.Delete(id); public void Clear() => Repo.Clear(); }";

        var ids = await AnalyzerHarness.DiagnoseAsync(Source);

        Assert.Single(ids, "QG0033");
    }

    [Fact]
    public async Task Abbreviations_are_reported_in_every_local_form()
    {
        const string Source = "using System; using System.Collections.Generic; class C { void M(IEnumerable<(int, int)> pairs, object o) {"
            + " for (var cfg = 0; cfg < 1; cfg++) { } foreach (var (src, _) in pairs) { } using var tmp = new System.IO.MemoryStream();"
            + " int.TryParse(\"1\", out var num); if (o is int opt) { } Func<int, int> f = req => req; try { } catch (Exception str) { } void Local(int cb) { } } }";

        var ids = await AnalyzerHarness.DiagnoseAsync(Source);

        Assert.Equal(8, ids.Count(id => id == "QG0051"));
    }

    [Fact]
    public async Task Invalid_negative_and_empty_config_values_fall_back_to_defaults()
    {
        var branches = string.Concat(Enumerable.Range(0, 11).Select(i => $"if (a == {i}) {{ return {i}; }}\n"));
        var source = $"class C {{ int M(int a) {{ {branches} return 0; }} }}";

        Assert.Contains("QG0003", await AnalyzerHarness.DiagnoseWithConfigAsync(source, ("qg_max_complexity", "-1")));
        Assert.Contains("QG0003", await AnalyzerHarness.DiagnoseWithConfigAsync(source, ("qg_max_complexity", "abc")));
        Assert.DoesNotContain("QG0003", await AnalyzerHarness.DiagnoseWithConfigAsync(source, ("qg_max_complexity", "50")));
        Assert.DoesNotContain("QG0050", await AnalyzerHarness.DiagnoseWithConfigAsync("class FooHelper { }", ("qg_banned_name_suffixes", string.Empty)));
    }

    [Fact]
    public async Task Service_locator_inside_registration_lambda_is_reported_outside_the_root() =>
        Assert.Contains("QG0036", await AnalyzerHarness.DiagnoseInAssemblyAsync(
            "using System;\nnamespace Microsoft.Extensions.DependencyInjection { public static class ServiceProviderServiceExtensions { public static T GetRequiredService<T>(this IServiceProvider p) where T : notnull => (T)p.GetService(typeof(T))!; } }\n"
            + "class C { Func<IServiceProvider, C> Factory = sp => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<C>(sp); }",
            "App.Features"));

    [Fact]
    public async Task Fan_out_ignores_calls_between_nested_and_outer_types()
    {
        var calls = string.Concat(Enumerable.Range(0, 11).Select(i => $"Outer.M{i}(); "));
        var methods = string.Concat(Enumerable.Range(0, 11).Select(i => $"public static void M{i}() {{ }} "));

        var ids = await AnalyzerHarness.DiagnoseAsync($"public class Outer {{ {methods} public class Nested {{ public void Run() {{ {calls} }} }} }}");

        Assert.DoesNotContain("QG0008", ids);
    }

    private static string Other(int count) =>
        "public class Other { " + string.Concat(Enumerable.Range(0, count).Select(i => $"public static void M{i}() {{ }} ")) + "}\n";
}
