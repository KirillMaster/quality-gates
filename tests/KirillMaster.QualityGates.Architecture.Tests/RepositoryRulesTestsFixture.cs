namespace KirillMaster.QualityGates.Architecture.Tests;

public sealed class RepositoryRulesFixtureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "qg-repo", Guid.NewGuid().ToString("N"));

    public RepositoryRulesFixtureTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Project_overriding_locked_setting_is_reported()
    {
        Write("A/A.csproj", "<Project><PropertyGroup><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup></Project>");

        Assert.Single(RepositoryRules.ProjectSettingsEnforced(root));
    }

    [Fact]
    public void Inline_and_floating_package_versions_are_reported()
    {
        Write("A/A.csproj", "<Project><ItemGroup><PackageReference Include=\"X\" Version=\"1.0.0\" /></ItemGroup></Project>");
        Write("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Y\" Version=\"1.*\" /></ItemGroup></Project>");

        Assert.Equal(2, RepositoryRules.PackageVersionsCentral(root).Count);
    }

    [Fact]
    public void Suppression_ratchet_blocks_growth_and_stale_baseline()
    {
        Write("quality/grandfathered/A.GlobalSuppressions.cs", "[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"a\", \"b\")]\n[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"c\", \"d\")]");
        Write("quality/baseline.json", "{\"maxGrandfatheredSuppressions\": 2}");
        Assert.Empty(RepositoryRules.SuppressionsUnderLimit(root));

        Write("quality/baseline.json", "{\"maxGrandfatheredSuppressions\": 1}");
        Assert.Contains("exceed baseline", Assert.Single(RepositoryRules.SuppressionsUnderLimit(root)), StringComparison.Ordinal);

        Write("quality/baseline.json", "{\"maxGrandfatheredSuppressions\": 5}");
        Assert.Contains("stale", Assert.Single(RepositoryRules.SuppressionsUnderLimit(root)), StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_pragma_counts_against_limit()
    {
        Write("src/A.cs", "#pragma warning disable CS0168\nclass A { }");

        Assert.Single(RepositoryRules.SuppressionsUnderLimit(root));
        Assert.Empty(RepositoryRules.SuppressionsUnderLimit(root, new RepositoryRulesOptions { InlineSuppressionLimit = 1 }));
    }

    [Fact]
    public void Nested_worktree_is_not_scanned()
    {
        Write("src/A.cs", "#pragma warning disable CS0168\nclass A { }");
        Write(".worktrees/feature/.git", "gitdir: ../../.git/worktrees/feature");
        Write(".worktrees/feature/src/A.cs", "#pragma warning disable CS0168\nclass A { }");
        Write(".worktrees/feature/A/A.csproj", "<Project />");

        Assert.Empty(RepositoryRules.SuppressionsUnderLimit(root, new RepositoryRulesOptions { InlineSuppressionLimit = 1 }));
        Assert.Empty(RepositoryRules.ProjectFiles(root));
    }

    [Fact]
    public void Project_reference_across_layers_is_reported_and_unloaded_project_found()
    {
        Write("Shop.Domain/Shop.Domain.csproj", "<Project><ItemGroup><ProjectReference Include=\"..\\Shop.Adapters\\Shop.Adapters.csproj\" /></ItemGroup></Project>");
        Write("Shop.Adapters/Shop.Adapters.csproj", "<Project />");

        Assert.Single(RepositoryRules.ProjectReferencesFollowLayers(root, ArchitectureConfig.Hexagonal("Shop")));
        Assert.Equal(["Project Shop.Adapters is not covered by the architecture tests"], RepositoryRules.EveryProjectLoaded(root, ["Shop.Domain"]));
    }
}
