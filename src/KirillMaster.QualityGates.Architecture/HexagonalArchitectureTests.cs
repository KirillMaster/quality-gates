using System.Reflection;
using ArchUnitNET.xUnit;
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
}
