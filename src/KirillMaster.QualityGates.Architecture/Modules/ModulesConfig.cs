using System.Text.Json;
using System.Text.Json.Serialization;
using ArchUnitNET.Domain;

namespace KirillMaster.QualityGates.Architecture.Modules;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModuleKind
{
    /// <summary>Vertical slice; may depend on Shared and Model modules but not on other features or Composition.</summary>
    Feature,

    /// <summary>Common code used by features; must not depend on Feature or Composition modules.</summary>
    Shared,

    /// <summary>Data-only model; must not depend on any other module.</summary>
    Model,

    /// <summary>Wiring code that may use everything; exempt from fan-out, can be capped.</summary>
    Composition,
}

/// <summary>One module. <see cref="Pattern"/> is a regex matched against a type namespace, then against its assembly name.</summary>
public sealed class ModuleSpec
{
    public string Name { get; set; } = string.Empty;

    public string Pattern { get; set; } = string.Empty;

    public ModuleKind Kind { get; set; } = ModuleKind.Feature;
}

/// <summary>Config of the generic module engine; with no modules every rule is a no-op.</summary>
public sealed class ModulesConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>Ordered; the first module whose pattern matches a type owns it. Types matching none are ignored.</summary>
    public List<ModuleSpec> Modules { get; set; } = [];

    /// <summary>Path of the known violations JSON; relative paths resolve against the repository root. Missing file means nothing is tolerated.</summary>
    public string BaselinePath { get; set; } = "quality/modules-baseline.json";

    /// <summary>Soft (false) tolerates the baseline edges; strict ignores the baseline for FeatureIsolation.</summary>
    public bool StrictFeatureIsolation { get; set; }

    /// <summary>Maximum distinct other modules a Feature module may depend on.</summary>
    public int ModuleFanOutMax { get; set; } = 6;

    /// <summary>Maximum types per Shared module; null disables SharedCap.</summary>
    public int? SharedMaxTypes { get; set; }

    /// <summary>Maximum types per Composition module (root ratchet: lower it as types move into features); null disables.</summary>
    public int? CompositionMaxTypes { get; set; }

    /// <summary>Regex on the full type name selecting orchestrator classes.</summary>
    public string OrchestratorPattern { get; set; } = "(Orchestrator|Coordinator)$";

    public int OrchestratorMaxMethods { get; set; } = 10;

    /// <summary>Maximum distinct types an orchestrator may depend on.</summary>
    public int OrchestratorMaxFanOut { get; set; } = 12;

    public static ModulesConfig FromJson(string json) =>
        JsonSerializer.Deserialize<ModulesConfig>(json, JsonOptions)
        ?? throw new InvalidOperationException("Modules config is empty.");

    public static ModulesConfig FromFile(string path) => FromJson(File.ReadAllText(path));

    public ModuleSpec? ModuleOf(IType type) =>
        Modules.FirstOrDefault(m => TypeGraph.MatchesNamespace(m.Pattern, type))
        ?? Modules.FirstOrDefault(m => TypeGraph.MatchesAssembly(m.Pattern, type));

    internal IEnumerable<string> Validate()
    {
        foreach (var duplicate in Modules.GroupBy(m => m.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            yield return $"Module '{duplicate.Key}' is defined more than once";
        }

        if (Modules.Any(m => m.Name.Length == 0 || m.Pattern.Length == 0))
        {
            yield return "Every module needs a name and a pattern";
        }
    }
}
