using System.Reflection;
using ArchUnitNET.xUnit;
using KirillMaster.QualityGates.Architecture.Hexagonal;
using KirillMaster.QualityGates.Architecture.Modules;
using Xunit;

namespace KirillMaster.QualityGates.Architecture;

/// <summary>
/// Inherit once per solution and supply the assemblies and the layer config. Inherited facts run the layering rules.
/// </summary>
public abstract class HexagonalArchitectureTests
{
    protected abstract IEnumerable<Assembly> Assemblies { get; }

    protected abstract ArchitectureConfig Config { get; }

    /// <summary>Repository root used for project-reference checks; return null to skip them.</summary>
    protected virtual string? RepositoryRoot => RepositoryRules.FindRepositoryRoot();

    /// <summary>Opt in to the generic module rules (feature isolation, shared purity and cap, model leaf, fan-out, orchestration); null skips them.</summary>
    protected virtual ModulesConfig? ModuleRulesConfig => null;

    /// <summary>Opt in to the hexagonal profile rules (adapter and runner direction, container-free core, port width, mirror types); null skips them.</summary>
    protected virtual HexagonalProfile? HexagonalRulesConfig => null;

    /// <summary>Also fail when a baseline entry no longer occurs or has shrunk, so the baseline can only get smaller.</summary>
    protected virtual bool FailOnStaleBaseline => false;

    [Fact]
    [Trait("Category", "Architecture")]
    public void Layers_depend_only_in_allowed_directions()
    {
        var architecture = ArchitectureRules.Load(Assemblies);
        foreach (var (_, rule) in ArchitectureRules.LayerRules(Config))
        {
            rule.Check(architecture);
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Layers_avoid_forbidden_namespaces()
    {
        var architecture = ArchitectureRules.Load(Assemblies);
        foreach (var (_, rule) in ArchitectureRules.ForbiddenNamespaceRules(Config))
        {
            rule.Check(architecture);
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Namespaces_have_no_cycles()
    {
        if (Config.CheckNamespaceCycles)
        {
            ArchitectureRules.NamespaceCycleRule(Config).Check(ArchitectureRules.Load(Assemblies));
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Types_have_no_new_cycles()
    {
        if (Config.CheckTypeCycles)
        {
            var path = Path.IsPathRooted(Config.TypeCycleBaselinePath) ? Config.TypeCycleBaselinePath : Path.Combine(RepositoryRoot ?? Directory.GetCurrentDirectory(), Config.TypeCycleBaselinePath);
            var assemblies = Assemblies.ToArray();
            var found = TypeCycles.New(TypeCycles.Find(ArchitectureRules.Load(assemblies), assemblies), TypeCycleBaseline.FromFile(path));
            Assert.True(found.Count == 0, "New type cycles (add to " + Config.TypeCycleBaselinePath + " only to tolerate): " + string.Join("; ", found.Select(c => string.Join(" <-> ", c))));
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Project_references_follow_layers()
    {
        var root = RepositoryRoot;
        if (root is not null)
        {
            Assert.Empty(RepositoryRules.ProjectReferencesFollowLayers(root, Config));
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Modules_follow_isolation_rules()
    {
        if (ModuleRulesConfig is { } config)
        {
            var assemblies = Assemblies.ToArray();
            var found = ModuleRules.All(TypeGraph.Build(ArchitectureRules.Load(assemblies), assemblies), config);
            AssertNoNewViolations(found, config.BaselinePath, config.StrictFeatureIsolation ? ModuleRuleIds.FeatureIsolation : null, ModuleRules.ActiveRules(config));
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Hexagonal_roles_follow_profile_rules()
    {
        if (HexagonalRulesConfig is { } profile)
        {
            var assemblies = Assemblies.ToArray();
            AssertNoNewViolations(HexagonalRules.All(TypeGraph.Build(ArchitectureRules.Load(assemblies), assemblies), profile), profile.Baseline, null, HexagonalRules.ActiveRules);
        }
    }

    private void AssertNoNewViolations(IReadOnlyList<RuleViolation> found, string baselinePath, string? strictRule, IReadOnlyList<string> rulesRun)
    {
        var root = RepositoryRoot;
        if (!Path.IsPathRooted(baselinePath) && root is null)
        {
            throw new InvalidOperationException("Baseline path '" + baselinePath + "' is relative but RepositoryRoot is null; override RepositoryRoot or use an absolute path.");
        }

        var path = Path.IsPathRooted(baselinePath) ? baselinePath : Path.Combine(root!, baselinePath);
        var baseline = RuleBaseline.FromFile(path);
        var fresh = found.Where(v => v.Rule == strictRule || !baseline.Tolerates(v)).ToArray();
        Assert.True(fresh.Length == 0, "New violations (add to " + baselinePath + " only to tolerate): " + string.Join("; ", fresh.Select(v => $"[{v.Rule}] {v.Key}#{v.Measure} {v.Message}")));
        if (FailOnStaleBaseline)
        {
            Assert.True(baseline.Stale(found, rulesRun).Count == 0, "Stale baseline entries in " + baselinePath + " (remove or lower them): " + string.Join("; ", baseline.Stale(found, rulesRun)));
        }
    }
}
