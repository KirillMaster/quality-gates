using System.Text.Json;
using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Architecture;

/// <summary>One architectural layer. <see cref="Pattern"/> is a regex matched against namespaces and project names.</summary>
public sealed class LayerConfig
{
    public string Name { get; set; } = string.Empty;

    public string Pattern { get; set; } = string.Empty;

    /// <summary>Names of other layers this layer may depend on (a layer may always depend on itself).</summary>
    public List<string> MayDependOn { get; set; } = [];

    /// <summary>Namespace prefixes this layer must not use at all (for example System.IO in the domain).</summary>
    public List<string> ForbiddenNamespaces { get; set; } = [];
}

public sealed class ArchitectureConfig
{
    public List<LayerConfig> Layers { get; set; } = [];

    public bool CheckNamespaceCycles { get; set; } = true;

    /// <summary>ArchUnitNET slice pattern for the cycle check, for example "MyApp.(*)" or "(**)" for every namespace.</summary>
    public string CycleSlicePattern { get; set; } = "(**)";

    public bool CheckTypeCycles { get; set; } = true;

    /// <summary>Path of the known type cycles JSON ({"cycles": [["A","B"]]}); relative paths resolve against the repository root. Missing file means no cycle is tolerated.</summary>
    public string TypeCycleBaselinePath { get; set; } = "quality/type-cycles.json";

    /// <summary>Default hexagonal layout: Domain, Application (ports), Adapters, Host. Namespaces and projects are named {root}.{Layer}[.*].</summary>
    public static ArchitectureConfig Hexagonal(string root)
    {
        var r = Regex.Escape(root);
        return new ArchitectureConfig
        {
            Layers =
            [
                new LayerConfig
                {
                    Name = "Domain",
                    Pattern = $@"^{r}\.Domain(\..*)?$",
                    ForbiddenNamespaces = ["System.IO", "System.Net", "System.Data", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore"],
                },
                new LayerConfig { Name = "Application", Pattern = $@"^{r}\.Application(\..*)?$", MayDependOn = ["Domain"] },
                new LayerConfig { Name = "Adapters", Pattern = $@"^{r}\.(Adapters|Infrastructure)(\..*)?$", MayDependOn = ["Application", "Domain"] },
                new LayerConfig { Name = "Host", Pattern = $@"^{r}\.(Host|Api|Cli)(\..*)?$", MayDependOn = ["Adapters", "Application", "Domain"] },
            ],
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public static ArchitectureConfig FromJson(string json) =>
        JsonSerializer.Deserialize<ArchitectureConfig>(json, JsonOptions)
        ?? throw new InvalidOperationException("Architecture config is empty.");

    public static ArchitectureConfig FromFile(string path) => FromJson(File.ReadAllText(path));

    public LayerConfig? LayerOf(string name) =>
        Layers.FirstOrDefault(l => Regex.IsMatch(name, l.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    internal IEnumerable<string> Validate()
    {
        var names = Layers.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var layer in Layers)
        {
            foreach (var unknown in layer.MayDependOn.Where(d => !names.Contains(d)))
            {
                yield return $"Layer '{layer.Name}' depends on unknown layer '{unknown}'";
            }
        }
    }
}
