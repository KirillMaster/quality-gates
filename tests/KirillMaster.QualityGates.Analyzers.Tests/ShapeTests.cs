namespace KirillMaster.QualityGates.Analyzers.Tests;

public sealed class ShapeTests
{
    private const string OptionsStubs = """
        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceCollection { }
            public sealed class OptionsBuilder<T> where T : class
            {
                public OptionsBuilder<T> Bind(object o) => this;
                public OptionsBuilder<T> ValidateOnStart() => this;
            }
            public static class OptionsServiceCollectionExtensions
            {
                public static OptionsBuilder<T> AddOptions<T>(this IServiceCollection s) where T : class => new();
                public static IServiceCollection Configure<T>(this IServiceCollection s, System.Action<T> a) where T : class => s;
            }
        }
        public sealed class Opts { }
        """;

    private static Task<string[]> Ids(string source, params (string Key, string Value)[] config) =>
        AnalyzerHarness.DiagnoseWithConfigAsync(source, config);

    [Fact]
    public async Task Port_primitive_id_and_money_are_reported()
    {
        var ids = await Ids("namespace App.Ports.Output { public interface IRepo { void Get(string orderId, System.Threading.CancellationToken t); void Pay(decimal amount); void Find(System.Guid id); } }");

        Assert.Equal(3, ids.Count(i => i == "QG0130"));
    }

    [Fact]
    public async Task Port_value_objects_and_plain_primitives_are_accepted()
    {
        var ids = await Ids("namespace App.Ports.Output { public readonly record struct OrderId(int V); public interface IRepo { void Get(OrderId orderId); void Name(string name, bool paid); } }");

        Assert.DoesNotContain("QG0130", ids);
    }

    [Fact]
    public async Task Primitive_id_outside_ports_is_accepted() =>
        Assert.DoesNotContain("QG0130", await Ids("namespace App.Services { public interface IRepo { void Get(string orderId); } }"));

    [Fact]
    public async Task Port_leaking_mutable_collection_or_queryable_is_reported()
    {
        var ids = await Ids("""
            using System.Collections.Generic; using System.Linq;
            namespace App.Ports.Output { public interface IRepo { List<string> A(); IQueryable<string> B(); string[] C(); Dictionary<string,int> D(); } }
            """);

        Assert.Equal(4, ids.Count(i => i == "QG0135"));
    }

    [Fact]
    public async Task Port_returning_read_only_collections_and_bytes_is_accepted()
    {
        var ids = await Ids("""
            using System.Collections.Generic; using System.Threading; using System.Threading.Tasks;
            namespace App.Ports.Output { public interface IRepo { Task<IReadOnlyList<string>> A(CancellationToken t); byte[] B(); void C(List<string> input); } }
            """);

        Assert.DoesNotContain("QG0135", ids);
    }

    [Fact]
    public async Task Async_port_method_without_cancellation_token_is_reported()
    {
        const string Port = "using System.Threading; using System.Threading.Tasks; namespace App.Ports.Input { public interface IHandler { Task Run(); Task<int> Get(CancellationToken t); int Sync(); } }";

        var ids = await Ids(Port);

        Assert.Single(ids, i => i == "QG0137");
    }

    [Fact]
    public async Task Technology_type_in_port_is_reported_once_per_type()
    {
        var ids = await Ids("namespace App.Ports.Output { public interface IFiles { System.IO.Stream Open(); void Send(System.Net.Http.HttpResponseMessage m); } }");

        Assert.Equal(2, ids.Count(i => i == "QG0143"));
        Assert.DoesNotContain("QG0146", ids);
    }

    [Fact]
    public async Task Port_type_whitelist_flags_unknown_external_types_and_accepts_listed_ones()
    {
        var unknown = await Ids("namespace App.Ports.Output { public interface IClock { void Set(System.Text.StringBuilder b); } }");
        var listed = await Ids("using System.Threading.Tasks; namespace App.Ports.Output { public interface IClock { Task<System.DateTimeOffset> Now(System.Threading.CancellationToken t); } }");
        var configured = await Ids("namespace App.Ports.Output { public interface IClock { void Set(System.Text.StringBuilder b); } }", ("qg_port_type_whitelist", "System.Text.StringBuilder"));

        Assert.Contains("QG0146", unknown);
        Assert.DoesNotContain("QG0146", listed);
        Assert.DoesNotContain("QG0146", configured);
    }

    [Fact]
    public async Task Public_core_type_is_reported_unless_it_is_part_of_a_port_signature()
    {
        const string Hidden = "namespace App.Core { public class Calculator { } }";
        const string Exposed = "namespace App.Core { public sealed class Order { } } namespace App.Ports.Output { public interface IRepo { App.Core.Order Get(); } }";

        Assert.Contains("QG0131", await Ids(Hidden));
        Assert.DoesNotContain("QG0131", await Ids(Exposed));
        Assert.DoesNotContain("QG0131", await Ids("namespace App.Core { internal sealed class Calculator { } }"));
        Assert.DoesNotContain("QG0131", await Ids("namespace App.Services { public class Calculator { } }"));
    }

    [Fact]
    public async Task Core_layer_property_makes_every_public_type_core() =>
        Assert.Contains("QG0131", await Ids("namespace App.Handlers { public sealed class Calculator { } }", ("build_property.QualityGatesLayer", "core")));

    [Fact]
    public async Task Unsealed_public_class_without_derived_types_is_reported()
    {
        Assert.Contains("QG0132", await Ids("namespace App.Adapters { public class Store { } }"));
        Assert.DoesNotContain("QG0132", await Ids("namespace App.Adapters { public sealed class Store { } }"));
        Assert.DoesNotContain("QG0132", await Ids("namespace App.Adapters { public class Store { } public sealed class Derived : Store { } }", ("qg_x", "1")));
        Assert.DoesNotContain("QG0132", await Ids("namespace App.Adapters { public class Store { public virtual void Run() { } } }"));
        Assert.DoesNotContain("QG0132", await Ids("namespace App.Other { public class Store { } }"));
    }

    [Fact]
    public async Task Public_setters_and_fields_in_model_are_reported()
    {
        var ids = await Ids("namespace App.Core.Model { public sealed class Order { public int Qty { get; set; } public string Name; public int Ok { get; init; } public int Hidden { get; private set; } } }");

        Assert.Equal(2, ids.Count(i => i == "QG0133"));
    }

    [Fact]
    public async Task Public_setters_outside_model_are_accepted() =>
        Assert.DoesNotContain("QG0133", await Ids("namespace App.Adapters { public sealed class Row { public int Qty { get; set; } } }"));

    [Fact]
    public async Task Hybrid_model_type_with_mutable_properties_and_logic_is_reported()
    {
        const string Hybrid = "namespace App.Core.Model { public sealed class Order { public int Qty { get; set; } public int Next() { if (Qty > 0) { return Qty; } return 0; } } }";
        const string Plain = "namespace App.Core.Model { public sealed class Order { public int Qty { get; set; } public int Next() => Qty + 1; } }";

        Assert.Contains("QG0134", await Ids(Hybrid));
        Assert.DoesNotContain("QG0134", await Ids(Plain));
    }

    [Fact]
    public async Task Options_registration_without_validate_on_start_is_reported()
    {
        var bare = await Ids(OptionsStubs + "\nnamespace App { using Microsoft.Extensions.DependencyInjection; public static class Reg { public static void A(IServiceCollection s) { s.AddOptions<Opts>().Bind(new object()); } public static void B(IServiceCollection s) { s.Configure<Opts>(o => { }); } } }");

        Assert.Equal(2, bare.Count(i => i == "QG0136"));
    }

    [Fact]
    public async Task Options_registration_with_validate_on_start_is_accepted()
    {
        var chained = await Ids(OptionsStubs + "\nnamespace App { using Microsoft.Extensions.DependencyInjection; public static class Reg { public static void A(IServiceCollection s) { s.AddOptions<Opts>().Bind(new object()).ValidateOnStart(); } } }");
        var variable = await Ids(OptionsStubs + "\nnamespace App { using Microsoft.Extensions.DependencyInjection; public static class Reg { public static void A(IServiceCollection s) { var b = s.AddOptions<Opts>(); b.ValidateOnStart(); } } }");

        Assert.DoesNotContain("QG0136", chained);
        Assert.DoesNotContain("QG0136", variable);
    }

    [Fact]
    public async Task Enum_switch_outside_declaring_feature_is_reported()
    {
        const string Enum = "namespace App.Orders.Model { public enum Status { New, Paid } }\n";
        const string Foreign = "namespace App.Billing { public sealed class Printer { public string Show(App.Orders.Model.Status s) => s switch { App.Orders.Model.Status.New => \"n\", _ => throw new System.ArgumentException() }; } }";
        const string Owner = "namespace App.Orders.Handlers { public sealed class Printer { public string Show(App.Orders.Model.Status s) { switch (s) { case App.Orders.Model.Status.New: return \"n\"; default: throw new System.ArgumentException(); } } } }";
        const string Adapter = "namespace App.Adapters.Db { public sealed class Printer { public string Show(App.Orders.Model.Status s) => s switch { App.Orders.Model.Status.New => \"n\", _ => throw new System.ArgumentException() }; } }";

        Assert.Contains("QG0138", await Ids(Enum + Foreign));
        Assert.DoesNotContain("QG0138", await Ids(Enum + Owner));
        Assert.DoesNotContain("QG0138", await Ids(Enum + Adapter));
    }

    [Fact]
    public async Task Value_object_with_public_constructor_or_without_create_is_reported()
    {
        var ids = await Ids("namespace App.Core.Model { public readonly record struct OrderId(int Value); }");

        Assert.Equal(2, ids.Count(i => i == "QG0139"));
    }

    [Fact]
    public async Task Value_object_with_private_constructor_and_result_factory_is_accepted()
    {
        var ids = await Ids("""
            namespace App.Core.Model
            {
                public sealed class Result<T> { }
                public sealed class OrderId
                {
                    private OrderId(int value) => Value = value;
                    public int Value { get; }
                    public static Result<OrderId> Create(int value) => new();
                }
                public sealed class Order { public Order(int a, int b) { A = a; B = b; } public int A { get; } public int B { get; } }
            }
            """);

        Assert.DoesNotContain("QG0139", ids);
    }

    [Fact]
    public async Task Contract_class_and_loose_record_properties_are_reported()
    {
        var ids = await Ids("""
            namespace App.Contracts
            {
                public sealed class PlainDto { public int A { get; set; } }
                public sealed record Loose { public int A { get; set; } public int B { get; init; } public required int C { get; init; } public string? D { get; init; } public int E { get; init; } = 3; }
                public sealed record Positional(int A, string B);
            }
            """);

        Assert.Equal(3, ids.Count(i => i == "QG0140"));
    }

    [Fact]
    public async Task Technology_words_in_core_and_port_names_are_reported()
    {
        var ids = await Ids("namespace App.Core { internal sealed class SqlOrderStore { public void SaveToRedis() { } public void Save() { } } } namespace App.Ports.Output { public interface IS3Storage { } public interface IOrderStore { } }");

        Assert.Equal(3, ids.Count(i => i == "QG0141"));
    }

    [Fact]
    public async Task Technology_words_are_configurable_and_ignored_in_adapters()
    {
        Assert.DoesNotContain("QG0141", await Ids("namespace App.Adapters { internal sealed class SqlOrderStore { } }"));
        Assert.DoesNotContain("QG0141", await Ids("namespace App.Core { internal sealed class EntityStore { } }"));
        Assert.Contains("QG0141", await Ids("namespace App.Core { internal sealed class EntityStore { } }", ("qg_technology_words", "Entity")));
    }

    [Fact]
    public async Task Persistence_attributes_in_model_are_reported()
    {
        var ids = await Ids("""
            using System.Text.Json.Serialization; using System.ComponentModel.DataAnnotations.Schema;
            namespace App.Core.Model { public sealed class Order { [JsonIgnore] public int A { get; } [Column("a")] public int B { get; } public int C { get; } } }
            """);

        Assert.Equal(2, ids.Count(i => i == "QG0142"));
    }

    [Fact]
    public async Task Persistence_attributes_in_adapters_are_accepted() =>
        Assert.DoesNotContain("QG0142", await Ids("using System.Text.Json.Serialization; namespace App.Adapters { public sealed class Row { [JsonIgnore] public int A { get; } } }"));

    [Fact]
    public async Task Floating_point_money_is_reported_and_decimal_is_accepted()
    {
        var ids = await Ids("namespace App { public sealed class Invoice { public double Total { get; } public decimal Fee { get; } public double Ratio { get; } public void Pay(float amount) { } } }");

        Assert.Equal(2, ids.Count(i => i == "QG0144"));
    }

    [Fact]
    public async Task Rounding_without_midpoint_mode_is_reported()
    {
        var ids = await Ids("namespace App { public sealed class M { public decimal A(decimal v) => decimal.Round(v, 2); public double B(double v) => System.Math.Round(v); public double C(double v) => System.Math.Round(v, 2, System.MidpointRounding.ToEven); public double D(double v) => System.Math.Floor(v); } }");

        Assert.Equal(2, ids.Count(i => i == "QG0145"));
    }

    [Fact]
    public async Task Method_that_returns_a_value_and_mutates_state_is_reported()
    {
        var ids = await Ids("namespace App { public sealed class Counter { private int _n; private readonly System.Collections.Generic.List<int> _l = new(); public int Bump() { _n++; return _n; } public int Add(int x) { _l.Add(x); return _l.Count; } public int Peek() => _n; public void Reset() { _n = 0; } } }");

        Assert.Equal(2, ids.Count(i => i == "QG0147"));
    }

    [Fact]
    public async Task Command_query_exemptions_are_honoured()
    {
        var ids = await Ids("""
            namespace App
            {
                public sealed class Result<T> { }
                public sealed class Box
                {
                    private int _n; private object? _cache;
                    public bool TryTake(out int v) { _n++; v = _n; return true; }
                    public Result<int> Do() { _n++; return new(); }
                    public Box With(int n) { _n = n; return this; }
                    public object Cached() => _cache ??= new object();
                    public int Local() { var n = 0; n++; return n; }
                }
            }
            """);

        Assert.DoesNotContain("QG0147", ids);
    }

    [Fact]
    public async Task Magic_numbers_are_reported_but_zero_one_and_minus_one_are_not()
    {
        var ids = await Ids("namespace App { public sealed class S { public int A() => 42; public int B() => 1 + 0 - -1; public double C() => 0.5; } }");

        Assert.Equal(2, ids.Count(i => i == "QG0150"));
    }

    [Fact]
    public async Task Named_constants_still_need_invariant_attribute_and_invariant_kinds_are_honoured()
    {
        const string Attribute = "namespace App { public enum InvariantKind { SchemaLimit, Protocol, Math, Identifier, Metric } public sealed class InvariantAttribute(InvariantKind k) : System.Attribute { } }\n";

        Assert.Contains("QG0150", await Ids("namespace App { public sealed class S { private const int Retries = 5; } }"));
        Assert.DoesNotContain("QG0150", await Ids(Attribute + "namespace App { public sealed class S { [Invariant(InvariantKind.SchemaLimit)] private const int MaxNameLength = 64; } }"));
        Assert.DoesNotContain("QG0150", await Ids(Attribute + "namespace App { public sealed class S { [Invariant(InvariantKind.Identifier)] private const int Port = 5432; } }"));
        Assert.Contains("QG0150", await Ids(Attribute + "namespace App { public sealed class S { [Invariant(InvariantKind.Metric)] private const int RetryCount = 5; } }"));
    }

    [Fact]
    public async Task Magic_numbers_in_attributes_enums_tests_and_composition_roots_are_accepted()
    {
        const string Source = "namespace App { public enum E { A = 5 } [System.Obsolete(\"x\", false)] public sealed class S { [System.ComponentModel.DefaultValue(7)] public int A { get; } public int B() => 42; } }";

        Assert.DoesNotContain("QG0150", await Ids(Source.Replace("public int B() => 42;", string.Empty, StringComparison.Ordinal)));
        Assert.DoesNotContain("QG0150", await AnalyzerHarness.DiagnoseInAssemblyAsync(Source, "App.CompositionRoot"));
        Assert.DoesNotContain("QG0150", await Ids(Source, ("build_property.QualityGatesLayer", "test")));
    }

    [Fact]
    public async Task Time_span_literals_are_reported_once_and_not_as_magic_numbers()
    {
        var ids = await Ids("namespace App { using System; public sealed class S { public TimeSpan A() => TimeSpan.FromSeconds(30); public TimeSpan B(int n) => TimeSpan.FromSeconds(n); } }");

        Assert.Single(ids, i => i == "QG0151");
        Assert.DoesNotContain("QG0150", ids);
    }

    [Fact]
    public async Task Urls_and_hosts_are_reported_and_plain_strings_are_not()
    {
        var ids = await Ids("namespace App { public sealed class S { public string A() => \"https://api.example.com/v1\"; public string B() => \"db.internal:5432\"; public string C() => \"10.0.0.5\"; public string D() => \"System.Net.Http\"; public string E() => \"hello world\"; public string F() => \"appsettings.json\"; } }");

        Assert.Equal(3, ids.Count(i => i == "QG0152"));
    }

    [Fact]
    public async Task Protocol_invariant_exempts_url_constant()
    {
        const string Attribute = "namespace App { public enum InvariantKind { Protocol } public sealed class InvariantAttribute(InvariantKind k) : System.Attribute { } }\n";

        Assert.DoesNotContain("QG0152", await Ids(Attribute + "namespace App { public sealed class S { [Invariant(InvariantKind.Protocol)] public const string Ns = \"http://schemas.xmlsoap.org/soap/envelope/\"; } }"));
    }

    [Fact]
    public async Task Shape_rules_are_silent_in_test_layer()
    {
        const string Source = "namespace App.Core.Model { public class Order { public int Qty { get; set; } public int A() => 42; } }";

        var ids = await Ids(Source, ("build_property.QualityGatesLayer", "test"));

        Assert.DoesNotContain(ids, i => string.Compare(i, "QG0130", StringComparison.Ordinal) >= 0);
    }
}
