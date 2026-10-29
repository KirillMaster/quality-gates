using System.Reflection;

namespace KirillMaster.QualityGates.Architecture.Tests;

public sealed class TypeCycleTests
{
    private const string Cycles = @"
namespace Shop
{
    public class A { public B? B { get; set; } }
    public class B { public C? C { get; set; } }
    public class C { public A? A { get; set; } }
    public class D { public A? A { get; set; } }
    public class Outer { public class Inner { public Outer? Parent { get; set; } } public Inner? Child { get; set; } }
    public class Lambdas { public int Run() { System.Func<Lambdas, int> f = x => x.Run(); return f(this); } }
}";

    private static IReadOnlyList<IReadOnlyList<string>> Find(Assembly assembly) => TypeCycles.Find(ArchitectureRules.Load([assembly]), [assembly]);

    [Fact]
    public void Finds_only_the_real_cycle()
    {
        var cycles = Find(FixtureCompiler.Compile("ShopTypeCycles", Cycles));

        Assert.Equal([["Shop.A", "Shop.B", "Shop.C"]], cycles);
    }

    [Fact]
    public void Acyclic_assembly_has_no_cycles() =>
        Assert.Empty(Find(FixtureCompiler.Compile("ShopNoTypeCycles", "namespace Shop { public class A { public B? B { get; set; } } public class B { } }")));

    [Fact]
    public void Baseline_tolerates_known_cycles_and_flags_new_ones()
    {
        var cycles = Find(FixtureCompiler.Compile("ShopTypeCyclesBaseline", Cycles));

        Assert.Empty(TypeCycles.New(cycles, TypeCycleBaseline.FromJson("""{"cycles": [["Shop.C", "Shop.A", "Shop.B"]]}""")));
        Assert.Single(TypeCycles.New(cycles, TypeCycleBaseline.FromJson("""{"cycles": [["Shop.A", "Shop.B"]]}""")));
        Assert.Single(TypeCycles.New(cycles, new TypeCycleBaseline()));
    }

    [Fact]
    public void Baseline_round_trips_and_missing_file_is_empty()
    {
        var baseline = TypeCycleBaseline.From([["Shop.B", "Shop.A"]]);

        Assert.True(TypeCycleBaseline.FromJson(baseline.ToJson()).Contains(["Shop.A", "Shop.B"]));
        Assert.Empty(TypeCycleBaseline.FromFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")).Cycles);
    }

    [Fact]
    public void Base_test_fails_on_new_cycle_and_passes_with_baseline()
    {
        var assembly = FixtureCompiler.Compile("ShopTypeCyclesBase", Cycles);
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        var config = new ArchitectureConfig { TypeCycleBaselinePath = file };

        Assert.ThrowsAny<Exception>(() => new Probe(assembly, config).Types_have_no_new_cycles());

        File.WriteAllText(file, TypeCycleBaseline.From(Find(assembly)).ToJson());
        new Probe(assembly, config).Types_have_no_new_cycles();
    }

    private sealed class Probe(Assembly assembly, ArchitectureConfig config) : HexagonalArchitectureTests
    {
        protected override IEnumerable<Assembly> Assemblies => [assembly];

        protected override ArchitectureConfig Config => config;
    }
}
