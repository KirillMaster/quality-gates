using System.Reflection;
using ArchUnitNET.xUnit;

namespace KirillMaster.QualityGates.Architecture.Tests;

public sealed class LayerRuleTests
{
    private const string Good = @"
namespace Shop.Domain { public class Order { } }
namespace Shop.Application { public interface IOrders { Shop.Domain.Order Find(); } }
namespace Shop.Adapters { public class SqlOrders : Shop.Application.IOrders { public Shop.Domain.Order Find() => new(); } }
namespace Shop.Host { public static class Program { public static object Build() => new Shop.Adapters.SqlOrders(); } }";

    private const string DomainUsesApplication = @"
namespace Shop.Application { public interface IOrders { } }
namespace Shop.Domain { public class Order { public Shop.Application.IOrders? Orders { get; set; } } }";

    private const string DomainUsesIo = @"
namespace Shop.Domain { public class Order { public bool Exists() => System.IO.File.Exists(""a""); } }";

    private const string Cycle = @"
namespace Shop.Domain.A { public class X { public Shop.Domain.B.Y? Y { get; set; } } }
namespace Shop.Domain.B { public class Y { public Shop.Domain.A.X? X { get; set; } } }";

    private static readonly ArchitectureConfig Config = ArchitectureConfig.Hexagonal("Shop");

    private static bool Passes(IEnumerable<(string Name, ArchUnitNET.Fluent.IArchRule Rule)> rules, Assembly assembly)
    {
        var architecture = ArchitectureRules.Load([assembly]);
        return rules.All(r => r.Rule.HasNoViolations(architecture));
    }

    [Fact]
    public void Clean_hexagonal_assembly_passes_all_rules()
    {
        var assembly = FixtureCompiler.Compile("ShopGood", Good);

        Assert.True(Passes(ArchitectureRules.LayerRules(Config), assembly));
        Assert.True(Passes(ArchitectureRules.ForbiddenNamespaceRules(Config), assembly));
        Assert.True(ArchitectureRules.NamespaceCycleRule(Config).HasNoViolations(ArchitectureRules.Load([assembly])));
    }

    [Fact]
    public void Domain_depending_on_application_violates_layer_rule() =>
        Assert.False(Passes(ArchitectureRules.LayerRules(Config), FixtureCompiler.Compile("ShopBadDeps", DomainUsesApplication)));

    [Fact]
    public void Domain_using_system_io_violates_forbidden_namespace_rule() =>
        Assert.False(Passes(ArchitectureRules.ForbiddenNamespaceRules(Config), FixtureCompiler.Compile("ShopBadIo", DomainUsesIo)));

    [Fact]
    public void Namespace_cycles_are_detected()
    {
        var assembly = FixtureCompiler.Compile("ShopCycle", Cycle);

        Assert.False(ArchitectureRules.NamespaceCycleRule(Config).HasNoViolations(ArchitectureRules.Load([assembly])));
    }

    [Fact]
    public void Config_round_trips_from_json_and_rejects_unknown_layers()
    {
        var config = ArchitectureConfig.FromJson("""{"layers":[{"name":"Core","pattern":"^X\\.Core","mayDependOn":[]},{"name":"Edge","pattern":"^X\\.Edge","mayDependOn":["Nope"]}]}""");

        Assert.Equal("Core", config.LayerOf("X.Core.Things")?.Name);
        Assert.Throws<InvalidOperationException>(() => ArchitectureRules.LayerRules(config));
    }
}
