using Microsoft.CodeAnalysis;

namespace KirillMaster.QualityGates.Analyzers.Tests;

public sealed class ReadabilityTests
{
    private const string Stubs =
        "namespace NSubstitute { public static class Substitute { public static T For<T>() where T : class => default!; } }\n"
        + "namespace Moq { public class Mock<T> where T : class { } }\n"
        + "namespace Microsoft.Extensions.Logging { public interface ILogger<T> { } }\n"
        + "namespace Sample.Ports.Output { public interface IRepo { } }\n"
        + "namespace Sample.Core { public class Concrete { } public interface INotPort { } }\n";

    private static string Test(string body, string name = "Does_thing_when_asked") =>
        "using Xunit;\npublic class Tests { [Fact] public void " + name + "() {\n" + body + "\n} }";

    private static Task<string[]> Run(string source, string assembly = "Sample", string? glossary = null, params (string, string)[] config) =>
        ReadabilityHarness.DiagnoseAsync(source, assembly, glossary, config);

    [Fact]
    public void Descriptors_use_the_b6_range_with_unique_ids_and_help_links()
    {
        var descriptors = ReadabilityHarness.Descriptors().Where(d => string.CompareOrdinal(d.Id, "QG0170") >= 0).DistinctBy(d => d.Id).ToArray();

        Assert.Equal(18, descriptors.Length);
        Assert.All(descriptors, d => Assert.InRange(int.Parse(d.Id.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture), 170, 199));
        Assert.All(descriptors, d => Assert.EndsWith(d.Id.ToLowerInvariant(), d.HelpLinkUri, StringComparison.Ordinal));
        Assert.All(descriptors, d => Assert.Equal(DiagnosticSeverity.Warning, d.DefaultSeverity));
    }

    [Theory]
    [InlineData("if (1 == 1) { }")]
    [InlineData("foreach (var x in new[] { 1 }) { }")]
    [InlineData("for (var i = 0; i < 2; i++) { }")]
    [InlineData("while (false) { }")]
    [InlineData("switch (1) { default: break; }")]
    public async Task T1_logic_in_test_is_reported(string body) =>
        Assert.Contains("QG0170", await Run(Test(body)));

    [Fact]
    public async Task T1_plain_test_and_exempt_assembly_are_accepted()
    {
        Assert.DoesNotContain("QG0170", await Run(Test("Assert.True(true);")));
        Assert.DoesNotContain("QG0170", await Run(Test("if (true) { }"), "Sample.ArchitectureTests"));
    }

    [Fact]
    public async Task T2_mock_of_non_port_is_reported()
    {
        var ids = await Run(Stubs + Test("var a = NSubstitute.Substitute.For<Sample.Core.Concrete>(); var b = new Moq.Mock<System.IDisposable>(); var c = new Moq.Mock<Sample.Core.INotPort>();"));

        Assert.Equal(3, ids.Count(i => i == "QG0171"));
    }

    [Fact]
    public async Task T2_own_port_and_logger_are_accepted() =>
        Assert.DoesNotContain("QG0171", await Run(Stubs + Test("var a = NSubstitute.Substitute.For<Sample.Ports.Output.IRepo>(); var b = new Moq.Mock<Microsoft.Extensions.Logging.ILogger<int>>();")));

    [Fact]
    public async Task T2_port_segments_are_configurable() =>
        Assert.DoesNotContain("QG0171", await Run(Stubs + Test("var a = NSubstitute.Substitute.For<Sample.Core.INotPort>();"), "Sample", null, ("qg_port_namespace_segments", "Core")));

    [Fact]
    public async Task T3_long_arrange_is_reported_with_act_marker()
    {
        var arrange = string.Concat(Enumerable.Repeat("var x = 1;\n", 16));

        Assert.Contains("QG0172", await Run(Test(arrange + "// Act\nvar y = 2;\nAssert.True(true);")));
        Assert.DoesNotContain("QG0172", await Run(Test(string.Concat(Enumerable.Repeat("var x = 1;\n", 15)) + "// Act\nvar y = 2;\nAssert.True(true);")));
    }

    [Fact]
    public async Task T3_without_marker_arrange_ends_before_statement_preceding_first_assert()
    {
        var arrange = string.Concat(Enumerable.Repeat("var x = 1;\n", 16));

        Assert.Contains("QG0172", await Run(Test(arrange + "var y = 2;\nAssert.True(true);")));
        Assert.DoesNotContain("QG0172", await Run(Test(arrange + "Assert.True(true);"), "Sample", null, ("qg_max_arrange_statements", "16")));
    }

    [Theory]
    [InlineData("Does_thing_when_asked", false)]
    [InlineData("Returns_empty", true)]
    [InlineData("Test1", true)]
    [InlineData("lower_case_name", true)]
    public async Task T5_test_name_follows_pattern(string name, bool reported)
    {
        var ids = await Run(Test("Assert.True(true);", name));

        Assert.Equal(reported, ids.Contains("QG0173"));
    }

    [Fact]
    public async Task T5_pattern_is_configurable() =>
        Assert.DoesNotContain("QG0173", await Run(Test("Assert.True(true);", "Returns_empty"), "Sample", null, ("qg_test_name_pattern", @"^\w+_\w+$")));

    [Fact]
    public async Task N3_unknown_word_is_reported_only_with_a_glossary()
    {
        const string Source = "class Frobnicator { public int OrderTotal; }";

        Assert.Contains("QG0174", await Run(Source, "Sample", "# project words\nacme"));
        Assert.DoesNotContain("QG0174", await Run(Source));
        Assert.DoesNotContain("QG0174", await Run(Source, "Sample", "frobnicator"));
    }

    [Fact]
    public async Task N3_checks_locals_and_ignores_acronyms_and_inflections()
    {
        Assert.Contains("QG0174", await Run("class C { int M() { var blorp = 1; return blorp; } }", "Sample", "acme"));
        Assert.DoesNotContain("QG0174", await Run("class C { int HttpId(int[] orders) { var cached = orders.Length; return cached; } }", "Sample", "acme"));
    }

    [Fact]
    public async Task N1_allowlist_exempts_named_type()
    {
        Assert.Contains("QG0050", await Run("class OrderManager { }"));
        Assert.DoesNotContain("QG0050", await Run("class OrderManager { }", "Sample", null, ("qg_banned_name_suffix_allowlist", "OrderManager")));
    }

    [Fact]
    public async Task R4_region_is_reported() =>
        Assert.Contains("QG0175", await Run("class C {\n#region Fields\nint x;\n#endregion\n}"));

    [Theory]
    [InlineData("// Author: John")]
    [InlineData("// created by John on Monday")]
    [InlineData("// JD 2019-03-04 fixed rounding")]
    [InlineData("/* @author jd */")]
    public async Task Z8_byline_is_reported(string comment) =>
        Assert.Contains("QG0176", await Run(comment + "\nclass C { }"));

    [Fact]
    public async Task Z8_ordinary_comment_is_accepted() =>
        Assert.DoesNotContain("QG0176", await Run("// Rounds half away from zero because the ledger requires it\nclass C { }"));

    [Fact]
    public async Task Z15_pragma_and_suppress_message_need_a_reason()
    {
        var bare = await Run("#pragma warning disable CS0168\nclass C { }");
        var reasoned = await Run("#pragma warning disable CS0168 // generated shim\nclass C { }");
        var attribute = await Run("using System.Diagnostics.CodeAnalysis;\n[SuppressMessage(\"Design\", \"CA1000\")] class C { }");
        var justified = await Run("using System.Diagnostics.CodeAnalysis;\n[SuppressMessage(\"Design\", \"CA1000\", Justification = \"public API\")] class C { }");

        Assert.Contains("QG0177", bare);
        Assert.DoesNotContain("QG0177", reasoned);
        Assert.Contains("QG0177", attribute);
        Assert.DoesNotContain("QG0177", justified);
    }

    [Fact]
    public async Task Z15_restore_is_not_reported() =>
        Assert.DoesNotContain("QG0177", await Run("#pragma warning restore CS0168\nclass C { }"));

    private const string Deep = "if (a) { if (b) { if (c) { if (d) { if (e) { return 1; } } } } }";

    [Fact]
    public async Task G4_complex_method_needs_a_why_comment()
    {
        var header = "class C { int M(bool a, bool b, bool c, bool d, bool e) {";

        Assert.Contains("QG0178", await Run(header + Deep + " return 0; } }"));
        Assert.DoesNotContain("QG0178", await Run("class C {\n// Branches mirror the legacy pricing table\nint M(bool a, bool b, bool c, bool d, bool e) {" + Deep + " return 0; } }"));
        Assert.DoesNotContain("QG0178", await Run(header + "\n// why: order matters\n" + Deep + " return 0; } }"));
        Assert.DoesNotContain("QG0178", await Run("class C {\n/// <summary>Docs only.</summary>\nint M(bool a) { return a ? 1 : 0; } }"));
    }

    [Fact]
    public async Task G4_xml_doc_does_not_count_as_a_why_comment() =>
        Assert.Contains("QG0178", await Run("class C {\n/// <summary>Docs.</summary>\nint M(bool a, bool b, bool c, bool d, bool e) {" + Deep + " return 0; } }"));

    [Theory]
    [InlineData("// temporary workaround for the null case")]
    [InlineData("// FIXME later")]
    [InlineData("// remove after the migration")]
    [InlineData("#pragma warning disable CS0168 // legacy")]
    public async Task K7_temp_marker_is_reported(string line) =>
        Assert.Contains("QG0179", await Run(line + "\nclass C { }"));

    [Fact]
    public async Task K7_ordinary_comment_is_accepted() =>
        Assert.DoesNotContain("QG0179", await Run("// Keeps the cache warm\nclass C { }"));

    [Fact]
    public async Task Z10_bool_name_needs_prefix()
    {
        var ids = await Run("class C { public bool Enabled { get; set; } public bool IsReady { get; set; } public bool Validate() => true; public bool HasItems() => true; }");

        Assert.Equal(2, ids.Count(i => i == "QG0180"));
    }

    [Fact]
    public async Task Z10_digit_suffix_is_reported_except_known_names()
    {
        var ids = await Run("class C { int M() { var user1 = 1; var user2 = 2; var sha256 = 3; return user1 + user2 + sha256; } }");

        Assert.Equal(2, ids.Count(i => i == "QG0181"));
    }

    [Fact]
    public async Task Z10_similar_names_in_one_scope_are_reported()
    {
        Assert.Contains("QG0182", await Run("class C { int M() { var customer = 1; var custumer = 2; return customer + custumer; } }"));
        Assert.DoesNotContain("QG0182", await Run("class C { int M(int[] items) { var item = items[0]; var total = items.Length; return item + total; } }"));
    }

    [Fact]
    public async Task Z10_plural_pair_is_not_similar() =>
        Assert.DoesNotContain("QG0182", await Run("class C { int M(int[] orders) { foreach (var order in orders) { return order; } return 0; } }"));

    [Fact]
    public async Task Z10_name_that_lies_about_its_type_is_reported()
    {
        var ids = await Run("class C { string accountList = \"\"; int isActive; System.Collections.Generic.List<int> idList = new(); int Pick(int[] valueDictionary) => 0; }");

        Assert.Equal(3, ids.Count(i => i == "QG0183"));
    }

    private const string Mixed =
        "namespace Sample.Core { public class Svc { void A() { } void B() { } void C() { }\n"
        + "public int Run(int[] xs) { A(); B(); C(); var t = 0; foreach (var x in xs) { t += x; } foreach (var x in xs) { t += x; } foreach (var x in xs) { t += x; } return t; }\n"
        + "public void Compose() { A(); B(); C(); }\n"
        + "public int Work(int[] xs) { var t = 0; foreach (var x in xs) { t += x; } foreach (var x in xs) { t += x; } foreach (var x in xs) { t += x; } return t; } } }";

    [Fact]
    public async Task L6_mixed_method_in_core_is_reported_once()
    {
        var ids = await Run(Mixed);

        Assert.Single(ids, "QG0184");
    }

    [Fact]
    public async Task L6_is_core_only_and_configurable()
    {
        Assert.DoesNotContain("QG0184", await Run(Mixed.Replace("Sample.Core", "Sample.Adapters", StringComparison.Ordinal)));
        Assert.DoesNotContain("QG0184", await Run(Mixed, "Sample", null, ("qg_min_low_statements", "4")));
        Assert.Contains("QG0184", await Run(Mixed.Replace("Sample.Core", "Sample.Services", StringComparison.Ordinal), "Sample", null, ("qg_core_namespace_segments", "Services")));
    }

    private const string Handler =
        "namespace Sample.Ports.Input { public interface IOrderHandler { int Handle(int[] xs); } }\n"
        + "namespace Sample.Core { public class H : Sample.Ports.Input.IOrderHandler {\n"
        + "int Count(int[] xs) => xs.Length;\n"
        + "public int Handle(int[] xs) { if (xs is null) { throw new System.ArgumentNullException(nameof(xs)); } var n = Count(xs); return n; }\n"
        + "public int Sum(int[] xs) { var t = 0; foreach (var x in xs) { t += x; } return t; } } }";

    [Fact]
    public async Task L7_handler_method_with_loop_is_reported_but_guarded_call_is_not() =>
        Assert.Single(await Run(Handler), "QG0185");

    [Fact]
    public async Task L7_type_outside_input_ports_is_ignored() =>
        Assert.DoesNotContain("QG0185", await Run(Handler.Replace("Ports.Input", "Ports.Output", StringComparison.Ordinal)));

    [Fact]
    public async Task L8_helper_above_caller_is_reported_and_below_is_not()
    {
        Assert.Contains("QG0186", await Run("class C { void Helper() { } public void Run() { Helper(); } }"));
        Assert.DoesNotContain("QG0186", await Run("class C { public void Run() { Helper(); } void Helper() { } }"));
        Assert.DoesNotContain("QG0186", await Run("class C { public void Run() { Helper(); } void Helper() { } void Other() { Helper(); } }"));
    }

    [Fact]
    public async Task L8_helper_called_from_a_caller_above_it_is_accepted() =>
        Assert.DoesNotContain("QG0186", await Run("class C { public void Run() { Helper(); } void Helper() { } public void Later() { Helper(); } }"));

    private const string Chain =
        "namespace Sample.Core { public class A { public B GetB() => new(); } public class B { public C GetC() => new(); } public class C { public void Run() { } }\n"
        + "public record P(Q Inner); public record Q(R Deep); public record R(int V);\n"
        + "public class Use { public void M(A a) { a.GetB().GetC().Run(); } public int Data(P p) => p.Inner.Deep.V; public int Sys(string s) => s.Trim().ToUpperInvariant().Length;\n"
        + "public int Linq(int[] xs) => xs.Select(x => x).Where(x => x > 0).Count(); } }";

    [Fact]
    public async Task R1_chain_through_behavioral_collaborators_is_reported_but_data_linq_and_system_are_not() =>
        Assert.Single(await Run("using System.Linq;\n" + Chain), "QG0187");
}
