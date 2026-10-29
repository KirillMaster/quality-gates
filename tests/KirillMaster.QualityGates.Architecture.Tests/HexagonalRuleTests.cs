using System.Reflection;
using KirillMaster.QualityGates.Architecture.Hexagonal;

namespace KirillMaster.QualityGates.Architecture.Tests;

public sealed class HexagonalRuleTests
{
    private const string Source = @"
namespace Shop.Core.Model { public class Order { public int Id { get; set; } public string? Name { get; set; } public System.IServiceProvider? Sp { get; set; } } }
namespace Shop.Core.Ports.Input
{
    public interface IPlaceOrder { void Place(); }
    public class OrderView { public int Id { get; set; } public string? Name { get; set; } public string? Note { get; set; } }
    public interface IFat { void A(); void B(); void C(); }
}
namespace Shop.Core.Ports.Output
{
    public interface IOrders { System.Linq.IQueryable<int> All(); void Save(); }
    public class OrderRow { public int Id { get; set; } public string? Name { get; set; } public string? Note { get; set; } }
    public class Unrelated { public decimal Price { get; set; } public bool Paid { get; set; } }
}
namespace Shop.Adapters.Postgres { public class PgOrders : Shop.Core.Ports.Output.IOrders { public System.Linq.IQueryable<int> All() => throw new System.NotImplementedException(); public void Save() { } public Shop.Adapters.Dynamo.DynamoCache? Cache { get; set; } public Shop.Runners.Api.Endpoint? E { get; set; } } }
namespace Shop.Adapters.Dynamo { public class DynamoCache { } }
namespace Shop.Runners.Api { public class Endpoint { public Shop.Core.Ports.Input.IPlaceOrder? P { get; set; } public Shop.Core.Ports.Output.IOrders? O { get; set; } public Shop.Adapters.Dynamo.DynamoCache? D { get; set; } } }
namespace Shop.Host { public class Composition { public Shop.Adapters.Postgres.PgOrders? A { get; set; } public Shop.Runners.Api.Endpoint? R { get; set; } } }";

    private static (TypeGraph Graph, Assembly Assembly) Load(string name)
    {
        var assembly = FixtureCompiler.Compile(name, Source);
        return (TypeGraph.Build(ArchitectureRules.Load([assembly]), [assembly]), assembly);
    }

    [Fact]
    public void Roles_come_from_naming_patterns()
    {
        var (graph, _) = Load("ShopHexRoles");
        var profile = new HexagonalProfile();

        var roles = graph.Types.ToDictionary(t => t.FullName, profile.RoleOf);

        Assert.Equal(
            [HexRole.Core, HexRole.PortsInput, HexRole.PortsOutput, HexRole.Adapters, HexRole.Runners, HexRole.Host],
            ["Shop.Core.Model.Order", "Shop.Core.Ports.Input.IFat", "Shop.Core.Ports.Output.IOrders", "Shop.Adapters.Dynamo.DynamoCache", "Shop.Runners.Api.Endpoint", "Shop.Host.Composition"].Select(n => roles[n]));
    }

    [Fact]
    public void Adapters_must_not_reference_each_other()
    {
        var (graph, _) = Load("ShopHexAdapters");

        Assert.Equal(["Postgres->Dynamo"], HexagonalRules.AdapterReferencesAdapter(graph, new HexagonalProfile()).Select(v => v.Key));
    }

    [Fact]
    public void Direction_flags_adapters_reaching_runners_and_core_reaching_the_container_side()
    {
        var (graph, _) = Load("ShopHexDirection");

        Assert.Equal(["Shop.Adapters.Postgres.PgOrders->Shop.Runners.Api.Endpoint"], HexagonalRules.AdapterDirection(graph, new HexagonalProfile()).Select(v => v.Key));
    }

    [Fact]
    public void Runners_reach_the_core_only_through_input_ports()
    {
        var (graph, _) = Load("ShopHexRunners");

        Assert.Equal(
            ["Shop.Runners.Api.Endpoint->Shop.Adapters.Dynamo.DynamoCache", "Shop.Runners.Api.Endpoint->Shop.Core.Ports.Output.IOrders"],
            HexagonalRules.RunnerHostReferences(graph, new HexagonalProfile()).Select(v => v.Key));
    }

    [Fact]
    public void Core_has_no_container_types()
    {
        var (graph, _) = Load("ShopHexContainer");

        Assert.Equal(["Shop.Core.Model.Order->System.IServiceProvider"], HexagonalRules.CoreNoContainer(graph, new HexagonalProfile()).Select(v => v.Key));
    }

    [Fact]
    public void Port_width_is_bounded()
    {
        var (graph, _) = Load("ShopHexWidth");

        Assert.Empty(HexagonalRules.PortWidth(graph, new HexagonalProfile()));
        var found = HexagonalRules.PortWidth(graph, new HexagonalProfile { PortMaxMembers = 2 });
        Assert.Equal([("Shop.Core.Ports.Input.IFat", 3)], found.Select(v => (v.Key, v.Measure)));
    }

    [Fact]
    public void Mirror_types_are_only_looked_for_between_input_and_output_ports()
    {
        var (graph, _) = Load("ShopHexMirror");

        Assert.Equal(["Shop.Core.Ports.Input.OrderView<->Shop.Core.Ports.Output.OrderRow"], HexagonalRules.NoMirrorTypes(graph, new HexagonalProfile()).Select(v => v.Key));
        Assert.Empty(HexagonalRules.NoMirrorTypes(graph, new HexagonalProfile { MirrorSimilarity = 1.1 }));
    }

    [Fact]
    public void Port_signatures_do_not_leak_technology_types()
    {
        var (graph, _) = Load("ShopHexTech");

        Assert.Equal(["Shop.Core.Ports.Output.IOrders->System.Linq.IQueryable`1"], HexagonalRules.PortNoTechnologyTypes(graph, new HexagonalProfile()).Select(v => v.Key));
    }

    [Fact]
    public void Base_test_fails_on_new_violations_and_passes_with_baseline()
    {
        var (graph, assembly) = Load("ShopHexBase");
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        var profile = new HexagonalProfile { Baseline = file };

        Assert.ThrowsAny<Exception>(() => new Probe(assembly, profile).Hexagonal_roles_follow_profile_rules());

        File.WriteAllText(file, RuleBaseline.From(HexagonalRules.All(graph, profile)).ToJson());
        new Probe(assembly, profile).Hexagonal_roles_follow_profile_rules();
    }

    private sealed class Probe(Assembly assembly, HexagonalProfile profile) : HexagonalArchitectureTests
    {
        protected override IEnumerable<Assembly> Assemblies => [assembly];

        protected override ArchitectureConfig Config => new();

        protected override string? RepositoryRoot => null;

        protected override HexagonalProfile? HexagonalRulesConfig => profile;
    }
}
