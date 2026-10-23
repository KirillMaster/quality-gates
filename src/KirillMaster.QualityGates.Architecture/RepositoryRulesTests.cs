using System.Reflection;
using Xunit;

namespace KirillMaster.QualityGates.Architecture;

/// <summary>Meta-tests that keep the quality configuration itself from eroding. Inherit once per solution.</summary>
public abstract class RepositoryRulesTests
{
    protected virtual string RepositoryRoot => RepositoryRules.FindRepositoryRoot();

    protected virtual RepositoryRulesOptions Options => new();

    /// <summary>Assemblies loaded by the architecture tests; when non-empty every production project must be among them.</summary>
    protected virtual IEnumerable<Assembly> LoadedAssemblies => [];

    [Fact]
    [Trait("Category", "Architecture")]
    public void Project_settings_are_enforced() => Assert.Empty(RepositoryRules.ProjectSettingsEnforced(RepositoryRoot, Options));

    [Fact]
    [Trait("Category", "Architecture")]
    public void Package_versions_are_central() => Assert.Empty(RepositoryRules.PackageVersionsCentral(RepositoryRoot));

    [Fact]
    [Trait("Category", "Architecture")]
    public void Suppressions_stay_under_baseline() => Assert.Empty(RepositoryRules.SuppressionsUnderLimit(RepositoryRoot, Options));

    [Fact]
    [Trait("Category", "Architecture")]
    public void Every_production_project_is_loaded()
    {
        var names = LoadedAssemblies.Select(a => a.GetName().Name ?? string.Empty).ToArray();
        if (names.Length > 0)
        {
            Assert.Empty(RepositoryRules.EveryProjectLoaded(RepositoryRoot, names));
        }
    }
}
