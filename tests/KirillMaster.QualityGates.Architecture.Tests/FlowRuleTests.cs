using System.Reflection;
using KirillMaster.QualityGates.Architecture.Hexagonal;

namespace KirillMaster.QualityGates.Architecture.Tests;

public sealed class FlowRuleTests
{
    private const string Source = @"
namespace Shop.Core { public class Result<T> { } }
namespace Shop.Core.Ports.Input.Orders
{
    public interface IPlaceOrderHandler { void Handle(); }
    public interface ICancelHandler { void Handle(); }
    public interface IFatHandler { void A(); void B(); }
    public interface IOtherHandler { void Handle(); }
}
namespace Shop.Core.Ports.Output.Orders { public interface IOrders { void Save(); } }
namespace Shop.Core.Features.Orders
{
    public class PlaceOrderHandler : Shop.Core.Ports.Input.Orders.IPlaceOrderHandler
    {
        public PlaceOrderHandler(Shop.Core.Ports.Output.Orders.IOrders orders, Shop.Core.Ports.Input.Orders.ICancelHandler other) { }
        public void Handle() { }
        public void Extra() { }
    }
    public class CancelHandler : Shop.Core.Ports.Input.Orders.ICancelHandler { public void Handle() { } public PlaceOrderHandler? P { get; set; } }
    public class FatHandler : Shop.Core.Ports.Input.Orders.IFatHandler, Shop.Core.Ports.Input.Orders.IOtherHandler { public void A() { } public void B() { } public void Handle() { } }
    public class OrderPolicy { public PlaceOrderHandler? H { get; set; } public Shop.Adapters.Postgres.Pg? A { get; set; } public Shop.Core.Result<int>? R { get; set; } }
}
namespace Shop.Adapters.Postgres
{
    public class Pg : Shop.Core.Ports.Output.Orders.IOrders { public void Save() { } }
    public class Bad { public Shop.Core.Features.Orders.PlaceOrderHandler? H { get; set; } }
}
namespace Shop.Runners.Api
{
    public class Endpoint
    {
        public Shop.Core.Ports.Input.Orders.IPlaceOrderHandler? P { get; set; }
        public Shop.Core.Ports.Output.Orders.IOrders? O { get; set; }
        public Shop.Adapters.Postgres.Pg? A { get; set; }
        public Shop.Core.Result<int> Keep(Shop.Core.Result<int> r) => r;
    }
}";

    private static TypeGraph Graph(string name, string source)
    {
        var assembly = FixtureCompiler.Compile(name, source);
        return TypeGraph.Build(ArchitectureRules.Load([assembly]), [assembly]);
    }

    [Fact]
    public void Flow_direction_flags_edges_pointing_back_up()
    {
        var found = FlowRules.FlowDirection(Graph("ShopFlowDirection", Source), new HexagonalProfile());

        Assert.Equal(
            ["Shop.Adapters.Postgres.Bad->Shop.Core.Features.Orders.PlaceOrderHandler", "Shop.Core.Features.Orders.OrderPolicy->Shop.Core.Features.Orders.PlaceOrderHandler"],
            found.Select(v => v.Key));
    }

    [Fact]
    public void No_level_skipping_flags_runners_and_domain_reaching_past_the_next_level()
    {
        var found = FlowRules.NoLevelSkipping(Graph("ShopFlowSkipping", Source), new HexagonalProfile());

        Assert.Equal(
            [
                "Shop.Core.Features.Orders.OrderPolicy->Shop.Adapters.Postgres.Pg",
                "Shop.Runners.Api.Endpoint->Shop.Adapters.Postgres.Pg",
                "Shop.Runners.Api.Endpoint->Shop.Core.Ports.Output.Orders.IOrders",
            ],
            found.Select(v => v.Key));
    }

    [Fact]
    public void Handlers_do_not_use_other_handlers_or_their_ports_but_may_use_their_own_port()
    {
        var found = FlowRules.HandlerIsolation(Graph("ShopFlowIsolation", Source), new HexagonalProfile());

        Assert.Equal(
            ["Shop.Core.Features.Orders.CancelHandler->Shop.Core.Features.Orders.PlaceOrderHandler", "Shop.Core.Features.Orders.PlaceOrderHandler->Shop.Core.Ports.Input.Orders.ICancelHandler"],
            found.Select(v => v.Key));
    }

    [Fact]
    public void Result_is_only_returned_outside_runners_and_host()
    {
        var found = FlowRules.ResultAtBoundary(Graph("ShopFlowResult", Source), new HexagonalProfile());

        Assert.Equal(["Shop.Core.Features.Orders.OrderPolicy.R"], found.Select(v => v.Key));
        Assert.Empty(FlowRules.ResultAtBoundary(Graph("ShopFlowResultAllowed", Source), new HexagonalProfile { ResultAllowedRoles = [HexRole.Core, HexRole.Runners] }));
    }

    [Fact]
    public void One_port_per_use_case_checks_members_ports_and_extra_public_methods()
    {
        var found = FlowRules.OnePortPerUseCase(Graph("ShopFlowOnePort", Source), new HexagonalProfile());

        Assert.Equal(
            [
                ("Shop.Core.Features.Orders.FatHandler:ports", 2),
                ("Shop.Core.Features.Orders.PlaceOrderHandler:public-methods", 1),
                ("Shop.Core.Ports.Input.Orders.IFatHandler:members", 2),
            ],
            found.Select(v => (v.Key, v.Measure)));
    }

    [Fact]
    public void A_second_implementation_is_a_violation_but_a_decorator_of_the_same_port_is_not()
    {
        var graph = Graph("ShopFlowDecorator", @"
namespace Shop.Core.Ports.Input.Orders { public interface IPlaceHandler { void Handle(); } }
namespace Shop.Core.Features.Orders
{
    public class PlaceHandler : Shop.Core.Ports.Input.Orders.IPlaceHandler { public void Handle() { } }
    public class LoggingPlaceHandler : Shop.Core.Ports.Input.Orders.IPlaceHandler { public LoggingPlaceHandler(Shop.Core.Ports.Input.Orders.IPlaceHandler inner) { } public void Handle() { } }
}");
        Assert.Empty(FlowRules.OnePortPerUseCase(graph, new HexagonalProfile()));

        var twin = Graph("ShopFlowTwin", @"
namespace Shop.Core.Ports.Input.Orders { public interface IPlaceHandler { void Handle(); } }
namespace Shop.Core.Features.Orders
{
    public class PlaceHandler : Shop.Core.Ports.Input.Orders.IPlaceHandler { public void Handle() { } }
    public class PlaceHandler2 : Shop.Core.Ports.Input.Orders.IPlaceHandler { public void Handle() { } }
}");
        Assert.Equal([("Shop.Core.Ports.Input.Orders.IPlaceHandler:implementations", 2)], FlowRules.OnePortPerUseCase(twin, new HexagonalProfile()).Select(v => (v.Key, v.Measure)));
    }

    [Fact]
    public void Folder_allowlist_flags_forbidden_folders_and_unregistered_features()
    {
        var graph = Graph("ShopFolders", @"
namespace Shop.Core.Features.Orders { public class A { } }
namespace Shop.Core.Features.Billing { public class B { } }
namespace Shop.Core.Ports.Output.Services { public class S { } public class T { } }
namespace Shop.Core.Ports.Output.Model { public class M { } }");

        Assert.Equal([("Shop.Core.Ports.Output.Services", 2)], FlowRules.FolderAllowlist(graph, new HexagonalProfile()).Select(v => (v.Key, v.Measure)));
        Assert.Equal(
            ["Shop.Core.Features.Billing", "Shop.Core.Ports.Output.Services"],
            FlowRules.FolderAllowlist(graph, new HexagonalProfile { Features = ["Orders"] }).Select(v => v.Key));
    }

    [Fact]
    public void Feature_mirror_needs_core_and_port_features_to_pair_up()
    {
        var graph = Graph("ShopMirror", @"
namespace Shop.Core.Features.Orders { public class A { } }
namespace Shop.Core.Features.Billing { public class B { } }
namespace Shop.Core.Ports.Input.Orders { public interface IPlaceHandler { void Handle(); } }
namespace Shop.Core.Ports.Output.Orders { public interface IOrders { void Save(); } }
namespace Shop.Core.Ports.Output.Audit { public interface IAudit { void Save(); } }");

        Assert.Equal(["Core:Billing", "Ports:Audit"], FlowRules.FeatureMirror(graph, new HexagonalProfile()).Select(v => v.Key));
        Assert.Empty(FlowRules.FeatureMirror(Graph("ShopMirrorNoFeatures", "namespace Shop.Core.Model { public class M { } } namespace Shop.Core.Ports.Input.Orders { public interface IPlaceHandler { void Handle(); } }"), new HexagonalProfile()));
    }

    [Fact]
    public void Handlers_must_be_referenced_by_tests_directly_or_through_their_port()
    {
        var graph = Graph("ShopRefs", @"
namespace Shop.Core.Ports.Input.Orders { public interface IAHandler { void Handle(); } public interface IBHandler { void Handle(); } public interface ICHandler { void Handle(); } }
namespace Shop.Core.Features.Orders
{
    public class AHandler : Shop.Core.Ports.Input.Orders.IAHandler { public void Handle() { } }
    public class BHandler : Shop.Core.Ports.Input.Orders.IBHandler { public void Handle() { } }
    public class CHandler : Shop.Core.Ports.Input.Orders.ICHandler { public void Handle() { } }
}
namespace Shop.Tests { public class T { public Shop.Core.Features.Orders.AHandler? A { get; set; } public Shop.Core.Ports.Input.Orders.IBHandler? B { get; set; } } }");

        Assert.Equal(["Shop.Core.Features.Orders.CHandler"], FlowRules.HandlerReferenced(graph, new HexagonalProfile()).Select(v => v.Key));
    }

    [Fact]
    public void Extra_rules_run_only_when_listed_and_unknown_names_are_rejected()
    {
        var graph = Graph("ShopExtra", Source);

        Assert.DoesNotContain(HexagonalRules.All(graph, new HexagonalProfile()), v => v.Rule == HexagonalRuleIds.FlowDirection);
        var profile = new HexagonalProfile { ExtraRules = [HexagonalRuleIds.FlowDirection, HexagonalRuleIds.HandlerIsolation] };
        Assert.Contains(HexagonalRules.All(graph, profile), v => v.Rule == HexagonalRuleIds.HandlerIsolation);
        Assert.Contains(HexagonalRuleIds.FlowDirection, HexagonalRules.ActiveRulesFor(profile));
        Assert.Throws<InvalidOperationException>(() => HexagonalRules.All(graph, new HexagonalProfile { ExtraRules = ["Nope"] }));
    }

    [Fact]
    public void Base_test_checks_extra_rules_against_the_baseline()
    {
        var assembly = FixtureCompiler.Compile("ShopExtraBase", Source);
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        var profile = new HexagonalProfile { Baseline = file, ExtraRules = [HexagonalRuleIds.FlowDirection] };

        Assert.ThrowsAny<Exception>(() => new Probe(assembly, profile).Hexagonal_roles_follow_profile_rules());

        var graph = TypeGraph.Build(ArchitectureRules.Load([assembly]), [assembly]);
        File.WriteAllText(file, RuleBaseline.From(HexagonalRules.All(graph, profile)).ToJson());
        new Probe(assembly, profile).Hexagonal_roles_follow_profile_rules();
    }

    [Fact]
    public void Test_types_have_no_role_and_do_not_count_as_handlers_or_violations()
    {
        var graph = Graph("ShopTestsExcluded", @"
namespace Shop.Core { public class Result<T> { } }
namespace Shop.Core.Ports.Input.Orders { public interface IAHandler { void Handle(); } }
namespace Shop.Core.Features.Orders { public class AHandler : Shop.Core.Ports.Input.Orders.IAHandler { public void Handle() { } } }
namespace Shop.Adapters.Postgres { public class Pg { } }
namespace Shop.Core.Tests
{
    public class FakeHandler : Shop.Core.Ports.Input.Orders.IAHandler { public Shop.Core.Result<int>? R { get; set; } public Shop.Adapters.Postgres.Pg? P { get; set; } public AHandlerTest? T { get; set; } public void Handle() { } public void More() { } }
    public class AHandlerTest { public Shop.Core.Features.Orders.AHandler? H { get; set; } }
}");
        var profile = new HexagonalProfile();

        Assert.Equal(HexRole.None, profile.RoleOf(graph.Types.Single(t => t.Name == "FakeHandler")));
        Assert.Empty(FlowRules.OnePortPerUseCase(graph, profile));
        Assert.Empty(FlowRules.ResultAtBoundary(graph, profile));
        Assert.Empty(FlowRules.FlowDirection(graph, profile));
        Assert.Empty(FlowRules.NoLevelSkipping(graph, profile));
        Assert.Empty(FlowRules.HandlerIsolation(graph, profile));
        Assert.Empty(FlowRules.HandlerReferenced(graph, profile));
        Assert.DoesNotContain(HexagonalRules.All(graph, profile), v => v.Key.Contains("FakeHandler", StringComparison.Ordinal));
    }

    [Fact]
    public void Di_registration_classes_are_outside_the_flow_and_output_port_dtos_are_not_level_skipping()
    {
        var graph = Graph("ShopComposition", @"
namespace Shop.Core.Ports.Input.Orders { public interface IPlaceHandler { void Handle(); } }
namespace Shop.Core.Ports.Output.Orders { public interface IOrders { void Save(); } public class OrderRow { public int Id { get; set; } } }
namespace Shop.Core.Features.Orders { public class PlaceHandler : Shop.Core.Ports.Input.Orders.IPlaceHandler { public void Handle() { } } }
namespace Shop.Adapters.Postgres
{
    public static class ServiceCollectionExtensions { public static void Add(Shop.Core.Features.Orders.PlaceHandler? h) { } }
    public class Bad { public Shop.Core.Features.Orders.PlaceHandler? H { get; set; } }
}
namespace Shop.Runner.Api { public class Endpoint { public Shop.Core.Ports.Output.Orders.OrderRow? Row { get; set; } public Shop.Core.Ports.Output.Orders.IOrders? O { get; set; } } }");
        var profile = new HexagonalProfile();

        Assert.Equal(HexRole.Runners, profile.RoleOf(graph.Types.Single(t => t.Name == "Endpoint")));
        Assert.Equal(["Shop.Adapters.Postgres.Bad->Shop.Core.Features.Orders.PlaceHandler"], FlowRules.FlowDirection(graph, profile).Select(v => v.Key));
        Assert.Equal(["Shop.Runner.Api.Endpoint->Shop.Core.Ports.Output.Orders.IOrders"], FlowRules.NoLevelSkipping(graph, profile).Select(v => v.Key));
    }

    [Fact]
    public void Core_internal_features_mirror_ports_and_unmatched_roles_fail_loudly()
    {
        var graph = Graph("ShopInternalMirror", @"
namespace Shop.Core.Internal.Orders { public class A { } }
namespace Shop.Core.Ports.Input.Orders { public interface IPlaceHandler { void Handle(); } }
namespace Shop.Core.Ports.Output.Audit { public interface IAudit { void Save(); } }");

        Assert.Equal(["Ports:Audit"], FlowRules.FeatureMirror(graph, new HexagonalProfile()).Select(v => v.Key));
        Assert.Empty(FlowRules.FolderAllowlist(graph, new HexagonalProfile()));
        var ex = Assert.Throws<InvalidOperationException>(() => HexagonalRules.All(graph, new HexagonalProfile { CorePattern = "^Nothing$", ExtraRules = [HexagonalRuleIds.FeatureMirror] }));
        Assert.Contains("FeatureMirror needs a Core type", ex.Message, StringComparison.Ordinal);
        var noTests = Assert.Throws<InvalidOperationException>(() => HexagonalRules.All(graph, new HexagonalProfile { ExtraRules = [HexagonalRuleIds.HandlerReferenced] }));
        Assert.Contains("needs test types", noTests.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_violations_are_keyed_by_signature_and_deduplicated()
    {
        var found = FlowRules.ResultAtBoundary(Graph("ShopResultKeys", @"
namespace Shop.Core { public class Result<T> { } public class Policy { public void M(Result<int> a, Result<int> b) { } public void M(Result<int> a) { } public Result<int> Ok() => new Result<int>(); } }"), new HexagonalProfile());

        Assert.Equal(3, found.Count);
        Assert.Equal(3, found.Select(v => v.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(found, v => Assert.StartsWith("Shop.Core.Policy.M(", v.Key, StringComparison.Ordinal));
    }

    private sealed class Probe(Assembly assembly, HexagonalProfile profile) : HexagonalArchitectureTests
    {
        protected override IEnumerable<Assembly> Assemblies => [assembly];

        protected override ArchitectureConfig Config => new();

        protected override string? RepositoryRoot => null;

        protected override HexagonalProfile? HexagonalRulesConfig => profile;
    }
}
