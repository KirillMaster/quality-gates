using System.Reflection;
using KirillMaster.QualityGates.Architecture.Modules;

namespace KirillMaster.QualityGates.Architecture.Tests;

public sealed class ModuleRuleTests
{
    private const string Source = @"
namespace Shop.Features.Bill { public class BillService { public Shop.Features.Fulfillment.Shipment? S { get; set; } public Shop.Shared.Money? M { get; set; } } }
namespace Shop.Features.Fulfillment { public class Shipment { } public class Dispatcher { public Shop.Shared.Money? M { get; set; } } }
namespace Shop.Features.Coupons { public class Coupon { public Shop.Root.Wiring? W { get; set; } } }
namespace Shop.Shared { public class Money { public Shop.Features.Coupons.Coupon? C { get; set; } } public class Clock { } }
namespace Shop.Model { public class Order { public Shop.Shared.Money? M { get; set; } } public class Line { } }
namespace Shop.Root { public class Wiring { } public class Startup { } }
namespace Shop.Flow
{
    public class CheckoutOrchestrator
    {
        public void A() { } public void B() { } public void C() { }
        public Shop.Shared.Money? M1 { get; set; } public Shop.Shared.Clock? M2 { get; set; } public Shop.Root.Wiring? M3 { get; set; }
    }
}";

    private static readonly List<ModuleSpec> Specs =
    [
        new() { Name = "Bill", Pattern = @"^Shop\.Features\.Bill$" },
        new() { Name = "Fulfillment", Pattern = @"^Shop\.Features\.Fulfillment$" },
        new() { Name = "Coupons", Pattern = @"^Shop\.Features\.Coupons$" },
        new() { Name = "Shared", Pattern = @"^Shop\.Shared$", Kind = ModuleKind.Shared },
        new() { Name = "Model", Pattern = @"^Shop\.Model$", Kind = ModuleKind.Model },
        new() { Name = "Root", Pattern = @"^Shop\.Root$", Kind = ModuleKind.Composition },
    ];

    private static (TypeGraph Graph, Assembly Assembly) Load(string name = "ShopModules", string source = Source)
    {
        var assembly = FixtureCompiler.Compile(name, source);
        return (TypeGraph.Build(ArchitectureRules.Load([assembly]), [assembly]), assembly);
    }

    [Fact]
    public void Feature_isolation_reports_feature_to_feature_and_feature_to_composition_edges()
    {
        var (graph, _) = Load();

        var found = ModuleRules.FeatureIsolation(graph, new ModulesConfig { Modules = Specs });

        Assert.Equal(["Bill->Fulfillment", "Coupons->Root"], found.Select(v => v.Key));
    }

    [Fact]
    public void Shared_purity_flags_shared_depending_on_a_feature()
    {
        var (graph, _) = Load("ShopModulesPurity");

        Assert.Equal(["Shared->Coupons"], ModuleRules.SharedPurity(graph, new ModulesConfig { Modules = Specs }).Select(v => v.Key));
    }

    [Fact]
    public void Model_is_a_leaf()
    {
        var (graph, _) = Load("ShopModulesLeaf");

        Assert.Equal(["Model->Shared"], ModuleRules.ModelIsLeaf(graph, new ModulesConfig { Modules = Specs }).Select(v => v.Key));
    }

    [Fact]
    public void Caps_are_off_by_default_and_count_types_when_set()
    {
        var (graph, _) = Load("ShopModulesCap");

        Assert.Empty(ModuleRules.SharedCap(graph, new ModulesConfig { Modules = Specs }));
        var shared = ModuleRules.SharedCap(graph, new ModulesConfig { Modules = Specs, SharedMaxTypes = 1 });
        Assert.Equal(("Shared", 2), (shared.Single().Key, shared.Single().Measure));
        Assert.Equal(["Root"], ModuleRules.CompositionCap(graph, new ModulesConfig { Modules = Specs, CompositionMaxTypes = 1 }).Select(v => v.Key));
    }

    [Fact]
    public void Module_fan_out_counts_distinct_modules()
    {
        var (graph, _) = Load("ShopModulesFanOut");

        Assert.Empty(ModuleRules.ModuleFanOut(graph, new ModulesConfig { Modules = Specs }));
        Assert.Equal(["Bill"], ModuleRules.ModuleFanOut(graph, new ModulesConfig { Modules = Specs, ModuleFanOutMax = 1 }).Select(v => v.Key));
    }

    [Fact]
    public void Orchestration_must_stay_thin()
    {
        var (graph, _) = Load("ShopModulesOrchestrator");

        Assert.Empty(ModuleRules.OrchestrationThin(graph, new ModulesConfig { Modules = Specs }));
        var found = ModuleRules.OrchestrationThin(graph, new ModulesConfig { Modules = Specs, OrchestratorMaxMethods = 2, OrchestratorMaxFanOut = 2 });
        Assert.Equal(["Shop.Flow.CheckoutOrchestrator:fan-out", "Shop.Flow.CheckoutOrchestrator:methods"], found.Select(v => v.Key));
    }

    [Fact]
    public void Duplicate_module_names_are_rejected()
    {
        var (graph, _) = Load("ShopModulesInvalid");

        Assert.Throws<InvalidOperationException>(() => ModuleRules.All(graph, new ModulesConfig { Modules = [Specs[0], Specs[0]] }));
    }

    [Fact]
    public void Config_loads_from_json_with_string_kinds()
    {
        var config = ModulesConfig.FromJson("""{"modules": [{"name": "S", "pattern": "^S$", "kind": "Shared"}], "sharedMaxTypes": 5}""");

        Assert.Equal((ModuleKind.Shared, 5), (config.Modules.Single().Kind, config.SharedMaxTypes));
    }

    [Fact]
    public void Soft_mode_tolerates_baseline_edges_and_strict_mode_does_not()
    {
        var (_, assembly) = Load("ShopModulesBase");
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        var config = new ModulesConfig { Modules = Specs, BaselinePath = file };

        Assert.ThrowsAny<Exception>(() => new Probe(assembly, config).Modules_follow_isolation_rules());

        var graph = TypeGraph.Build(ArchitectureRules.Load([assembly]), [assembly]);
        File.WriteAllText(file, RuleBaseline.From(ModuleRules.All(graph, config)).ToJson());
        new Probe(assembly, config).Modules_follow_isolation_rules();

        config.StrictFeatureIsolation = true;
        Assert.ThrowsAny<Exception>(() => new Probe(assembly, config).Modules_follow_isolation_rules());
    }

    [Fact]
    public void Baseline_only_shrinks()
    {
        var (graph, assembly) = Load("ShopModulesShrink");
        var config = new ModulesConfig { Modules = Specs };
        var found = ModuleRules.All(graph, config);
        var baseline = RuleBaseline.From(found);

        Assert.Empty(baseline.New(found));
        Assert.Empty(baseline.Stale(found));
        Assert.Single(baseline.New([.. found, new RuleViolation("FeatureIsolation", "Bill->Coupons", 1, string.Empty)]));
        Assert.Single(baseline.New([found[0] with { Measure = found[0].Measure + 1 }]));
        Assert.Single(baseline.Stale(found.Skip(1)));

        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, RuleBaseline.From([.. found, new RuleViolation("FeatureIsolation", "Gone->Away", 1, string.Empty)]).ToJson());
        config.BaselinePath = file;
        Assert.ThrowsAny<Exception>(() => new Probe(assembly, config, failOnStale: true).Modules_follow_isolation_rules());
        new Probe(assembly, config).Modules_follow_isolation_rules();
    }

    [Fact]
    public void Shared_cap_counts_only_top_level_types()
    {
        var (graph, _) = Load("ShopModulesNested", "namespace Shop.Shared { public class Money { public class Inner { } public class Other { } } public class Clock { } }");

        Assert.Empty(ModuleRules.SharedCap(graph, new ModulesConfig { Modules = Specs, SharedMaxTypes = 2 }));
        Assert.Single(ModuleRules.SharedCap(graph, new ModulesConfig { Modules = Specs, SharedMaxTypes = 1 }));
    }

    [Fact]
    public void Every_rule_entry_point_validates_the_config()
    {
        var (graph, _) = Load("ShopModulesValidate");
        var invalid = new ModulesConfig { Modules = [Specs[0], Specs[0]], SharedMaxTypes = 1, CompositionMaxTypes = 1 };

        Assert.Throws<InvalidOperationException>(() => ModuleRules.FeatureIsolation(graph, invalid));
        Assert.Throws<InvalidOperationException>(() => ModuleRules.SharedPurity(graph, invalid));
        Assert.Throws<InvalidOperationException>(() => ModuleRules.ModelIsLeaf(graph, invalid));
        Assert.Throws<InvalidOperationException>(() => ModuleRules.SharedCap(graph, invalid));
        Assert.Throws<InvalidOperationException>(() => ModuleRules.CompositionCap(graph, invalid));
        Assert.Throws<InvalidOperationException>(() => ModuleRules.ModuleFanOut(graph, invalid));
        Assert.Throws<InvalidOperationException>(() => ModuleRules.OrchestrationThin(graph, invalid));
    }

    [Fact]
    public void Stale_check_is_limited_to_the_rules_that_ran()
    {
        var (graph, assembly) = Load("ShopModulesScope");
        var config = new ModulesConfig { Modules = Specs };
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, RuleBaseline.From([.. ModuleRules.All(graph, config), new RuleViolation("PortWidth", "Shop.IFat", 12, string.Empty)]).ToJson());
        config.BaselinePath = file;

        new Probe(assembly, config, failOnStale: true).Modules_follow_isolation_rules();
        Assert.Single(RuleBaseline.FromFile(file).Stale([], ["PortWidth"]));
    }

    [Fact]
    public void Relative_baseline_without_repository_root_is_an_error()
    {
        var (_, assembly) = Load("ShopModulesRelative");

        var error = Assert.Throws<InvalidOperationException>(() => new Probe(assembly, new ModulesConfig { Modules = Specs }).Modules_follow_isolation_rules());

        Assert.Contains("RepositoryRoot", error.Message);
    }

    [Fact]
    public void Remap_renames_keys_and_keeps_measures()
    {
        var baseline = RuleBaseline.FromJson("""{"rules": {"FeatureIsolation": {"Bill->Fulfillment": 3, "Old->Other": 2, "Same->Keep": 1}, "ModuleFanOut": {"Old": 7, "New": 9}}}""");

        var remapped = baseline.Remap(new Dictionary<string, string> { ["Old->Other"] = "New->Other", ["Old"] = "New" });

        Assert.Equal(["Bill->Fulfillment", "New->Other", "Same->Keep"], remapped.Rules["FeatureIsolation"].Keys.Order());
        Assert.Equal(2, remapped.Rules["FeatureIsolation"]["New->Other"]);
        Assert.Equal(9, remapped.Rules["ModuleFanOut"]["New"]);
        Assert.Single(remapped.Rules["ModuleFanOut"]);
        Assert.True(baseline.Rules["FeatureIsolation"].ContainsKey("Old->Other"));
    }

    [Fact]
    public void Without_config_the_base_test_is_a_no_op() =>
        new Probe(Load("ShopModulesOff").Assembly, null).Modules_follow_isolation_rules();

    private sealed class Probe(Assembly assembly, ModulesConfig? modules, bool failOnStale = false) : HexagonalArchitectureTests
    {
        protected override IEnumerable<Assembly> Assemblies => [assembly];

        protected override ArchitectureConfig Config => new();

        protected override string? RepositoryRoot => null;

        protected override ModulesConfig? ModuleRulesConfig => modules;

        protected override bool FailOnStaleBaseline => failOnStale;
    }
}
