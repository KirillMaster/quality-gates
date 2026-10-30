using KirillMaster.QualityGates.Architecture.Modules;

namespace KirillMaster.QualityGates.Architecture.Tests;

public sealed class StableDependencyTests
{
    private const string Source = @"
namespace Mod.A { public class T { public Mod.S.T? S { get; set; } } }
namespace Mod.B { public class T { public Mod.S.T? S { get; set; } } }
namespace Mod.C { public class T { public Mod.S.T? S { get; set; } } }
namespace Mod.S { public class T { public Mod.U.T? U { get; set; } } }
namespace Mod.U { public class T { public Mod.X.T? X { get; set; } public Mod.Y.T? Y { get; set; } public Mod.Z.T? Z { get; set; } } }
namespace Mod.X { public class T { } }
namespace Mod.Y { public class T { } }
namespace Mod.Z { public class T { } }";

    private static ModulesConfig Config(bool enforce = false) =>
        new()
        {
            EnforceStableDependencies = enforce,
            Modules = [.. "ABCSUXYZ".Select(m => new ModuleSpec { Name = m.ToString(), Pattern = @"^Mod\." + m + "$" })],
        };

    private static TypeGraph Graph(string name)
    {
        var assembly = FixtureCompiler.Compile(name, Source);
        return TypeGraph.Build(ArchitectureRules.Load([assembly]), [assembly]);
    }

    [Fact]
    public void A_stable_module_depending_on_an_unstable_one_is_reported_with_the_gap()
    {
        var found = ModuleRules.StableDependencies(Graph("ModStable"), Config());

        Assert.Equal([("S->U", 50)], found.Select(v => (v.Key, v.Measure)));
    }

    [Fact]
    public void Weakly_coupled_modules_and_a_wide_epsilon_are_ignored()
    {
        var graph = Graph("ModStableLoose");

        Assert.Empty(ModuleRules.StableDependencies(graph, new ModulesConfig { Modules = Config().Modules, StableDependenciesEpsilon = 0.6 }));
        Assert.Empty(ModuleRules.StableDependencies(graph, new ModulesConfig { Modules = Config().Modules, StableDependenciesMinCoupling = 5 }));
    }

    [Fact]
    public void It_is_a_warning_until_enforced()
    {
        var graph = Graph("ModStableEnforce");

        Assert.DoesNotContain(ModuleRules.All(graph, Config()), v => v.Rule == ModuleRuleIds.StableDependencies);
        Assert.Contains(ModuleRules.All(graph, Config(true)), v => v.Rule == ModuleRuleIds.StableDependencies);
        Assert.Contains(ModuleRuleIds.StableDependencies, ModuleRules.ActiveRules(Config(true)));
        Assert.DoesNotContain(ModuleRuleIds.StableDependencies, ModuleRules.ActiveRules(Config()));
    }
}
