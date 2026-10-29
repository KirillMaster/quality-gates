using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KirillMaster.QualityGates.Architecture;

public sealed class RepositoryRulesOptions
{
    /// <summary>MSBuild properties a project file must not set (they are owned by Directory.Build.props / the Build package).</summary>
    public IReadOnlyList<string> LockedSettings { get; init; } =
        ["TreatWarningsAsErrors", "Nullable", "AnalysisLevel", "WarningsNotAsErrors", "CodeAnalysisTreatWarningsAsErrors", "QualityGatesEnabled"];

    /// <summary>Inline suppressions (pragma / SuppressMessage) allowed outside quality/grandfathered.</summary>
    public int InlineSuppressionLimit { get; init; }

    /// <summary>Relative path of the ratchet file: {"maxGrandfatheredSuppressions": N}. Missing file means 0.</summary>
    public string BaselinePath { get; init; } = "quality/baseline.json";

    /// <summary>When true the baseline must equal the real count, so it can only ever go down.</summary>
    public bool RequireTightBaseline { get; init; } = true;
}

/// <summary>Repository-level meta rules; each returns a list of violations (empty means green).</summary>
public static class RepositoryRules
{
    private static readonly Regex Suppression = new(@"#pragma\s+warning\s+disable|SuppressMessage\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<string> ProjectFiles(string root) =>
        [.. EnumerateFiles(root, "*.csproj").Order(StringComparer.Ordinal)];

    public static IReadOnlyDictionary<string, string[]> ProjectReferences(string root) =>
        ProjectFiles(root).ToDictionary(
            p => Path.GetFileNameWithoutExtension(p),
            p => XDocument.Load(p).Descendants("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(((string?)e.Attribute("Include") ?? string.Empty).Replace('\\', '/')))
                .ToArray(),
            StringComparer.Ordinal);

    public static IReadOnlyList<string> ProjectSettingsEnforced(string root, RepositoryRulesOptions? options = null)
    {
        options ??= new RepositoryRulesOptions();
        var violations = new List<string>();
        foreach (var file in ProjectFiles(root))
        {
            foreach (var element in XDocument.Load(file).Descendants().Where(e => options.LockedSettings.Contains(e.Name.LocalName, StringComparer.Ordinal)))
            {
                violations.Add($"{Path.GetFileName(file)}: overrides {element.Name.LocalName}");
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> PackageVersionsCentral(string root)
    {
        var violations = new List<string>();
        foreach (var file in ProjectFiles(root))
        {
            violations.AddRange(XDocument.Load(file).Descendants()
                .Where(e => e.Name.LocalName == "PackageReference" && (e.Attribute("Version") is not null || e.Attribute("VersionOverride") is not null))
                .Select(e => $"{Path.GetFileName(file)}: inline version for {e.Attribute("Include")?.Value}"));
        }

        var central = Path.Combine(root, "Directory.Packages.props");
        if (!File.Exists(central))
        {
            violations.Add("Directory.Packages.props not found (central package management required)");
            return violations;
        }

        violations.AddRange(XDocument.Load(central).Descendants()
            .Where(e => e.Name.LocalName is "PackageVersion" or "GlobalPackageReference")
            .Where(e => (e.Attribute("Version")?.Value ?? "*").IndexOfAny(['*', '[', '(', ',']) >= 0)
            .Select(e => $"Directory.Packages.props: floating version for {e.Attribute("Include")?.Value}"));
        return violations;
    }

    public static IReadOnlyList<string> SuppressionsUnderLimit(string root, RepositoryRulesOptions? options = null)
    {
        options ??= new RepositoryRulesOptions();
        var violations = new List<string>();
        var grandfathered = 0;
        var inline = 0;
        foreach (var file in EnumerateFiles(root, "*.cs"))
        {
            var count = Suppression.Count(File.ReadAllText(file));
            if (file.Replace('\\', '/').Contains("/quality/grandfathered/", StringComparison.Ordinal))
            {
                grandfathered += count;
            }
            else
            {
                inline += count;
            }
        }

        if (inline > options.InlineSuppressionLimit)
        {
            violations.Add($"{inline} inline suppressions, limit {options.InlineSuppressionLimit}");
        }

        var baseline = ReadBaseline(Path.Combine(root, options.BaselinePath));
        if (grandfathered > baseline)
        {
            violations.Add($"{grandfathered} grandfathered suppressions exceed baseline {baseline} (ratchet: fix new violations instead)");
        }
        else if (options.RequireTightBaseline && grandfathered < baseline)
        {
            violations.Add($"Baseline is stale: {grandfathered} grandfathered suppressions left, lower maxGrandfatheredSuppressions from {baseline}");
        }

        return violations;
    }

    public static IReadOnlyList<string> EveryProjectLoaded(string root, IEnumerable<string> loadedAssemblyNames, Func<string, bool>? isProduction = null)
    {
        isProduction ??= name => !name.EndsWith(".Tests", StringComparison.Ordinal) && !name.EndsWith(".Test", StringComparison.Ordinal);
        var loaded = loadedAssemblyNames.ToHashSet(StringComparer.Ordinal);
        return [.. ProjectReferences(root).Keys.Where(isProduction).Where(p => !loaded.Contains(p)).Order(StringComparer.Ordinal).Select(p => $"Project {p} is not covered by the architecture tests")];
    }

    public static IReadOnlyList<string> ProjectReferencesFollowLayers(string root, ArchitectureConfig config)
    {
        var violations = new List<string>();
        foreach (var (project, references) in ProjectReferences(root))
        {
            var from = config.LayerOf(project);
            if (from is null)
            {
                continue;
            }

            foreach (var reference in references)
            {
                var to = config.LayerOf(reference);
                if (to is not null && to.Name != from.Name && !from.MayDependOn.Contains(to.Name, StringComparer.Ordinal))
                {
                    violations.Add($"{project} ({from.Name}) references {reference} ({to.Name})");
                }
            }
        }

        return violations;
    }

    public static string FindRepositoryRoot(string? start = null)
    {
        var env = Environment.GetEnvironmentVariable("QG_REPO_ROOT");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        for (var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any())
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found; set QG_REPO_ROOT.");
    }

    private static int ReadBaseline(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("maxGrandfatheredSuppressions", out var value) ? value.GetInt32() : 0;
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Split('/').Any(s => s is "bin" or "obj" or "node_modules" or ".git" or "artifacts"));
}
