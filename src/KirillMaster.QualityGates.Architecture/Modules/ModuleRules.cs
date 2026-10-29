using System.Text.RegularExpressions;
using ArchUnitNET.Domain;

namespace KirillMaster.QualityGates.Architecture.Modules;

public static class ModuleRuleIds
{
    public const string FeatureIsolation = "FeatureIsolation";
    public const string SharedPurity = "SharedPurity";
    public const string SharedCap = "SharedCap";
    public const string CompositionCap = "CompositionCap";
    public const string ModelIsLeaf = "ModelIsLeaf";
    public const string ModuleFanOut = "ModuleFanOut";
    public const string OrchestrationThin = "OrchestrationThin";
}

/// <summary>Generic module rules over a <see cref="TypeGraph"/>; each returns every violation, the caller applies the baseline.</summary>
public static class ModuleRules
{
    public static IReadOnlyList<RuleViolation> All(TypeGraph graph, ModulesConfig config)
    {
        return [.. FeatureIsolation(graph, config), .. SharedPurity(graph, config), .. SharedCap(graph, config), .. CompositionCap(graph, config),
            .. ModelIsLeaf(graph, config), .. ModuleFanOut(graph, config), .. OrchestrationThin(graph, config)];
    }

    /// <summary>A feature may depend only on Shared, Model and itself; never on another feature or Composition.</summary>
    public static IReadOnlyList<RuleViolation> FeatureIsolation(TypeGraph graph, ModulesConfig config) =>
        Edges(graph, config, ModuleRuleIds.FeatureIsolation, (from, to) => from.Kind == ModuleKind.Feature && to.Kind is ModuleKind.Feature or ModuleKind.Composition,
            (from, to) => $"Feature {from} must not depend on {to}");

    /// <summary>Shared modules must not depend on Feature or Composition modules.</summary>
    public static IReadOnlyList<RuleViolation> SharedPurity(TypeGraph graph, ModulesConfig config) =>
        Edges(graph, config, ModuleRuleIds.SharedPurity, (from, to) => from.Kind == ModuleKind.Shared && to.Kind is ModuleKind.Feature or ModuleKind.Composition,
            (from, to) => $"Shared {from} must not depend on {to}");

    /// <summary>Model modules are leaves: they depend on no other module.</summary>
    public static IReadOnlyList<RuleViolation> ModelIsLeaf(TypeGraph graph, ModulesConfig config) =>
        Edges(graph, config, ModuleRuleIds.ModelIsLeaf, (from, _) => from.Kind == ModuleKind.Model,
            (from, to) => $"Model {from} must not depend on {to}");

    public static IReadOnlyList<RuleViolation> SharedCap(TypeGraph graph, ModulesConfig config) =>
        Cap(graph, config, ModuleKind.Shared, config.SharedMaxTypes, ModuleRuleIds.SharedCap);

    public static IReadOnlyList<RuleViolation> CompositionCap(TypeGraph graph, ModulesConfig config) =>
        Cap(graph, config, ModuleKind.Composition, config.CompositionMaxTypes, ModuleRuleIds.CompositionCap);

    /// <summary>A feature depends on at most <see cref="ModulesConfig.ModuleFanOutMax"/> distinct other modules.</summary>
    public static IReadOnlyList<RuleViolation> ModuleFanOut(TypeGraph graph, ModulesConfig config) =>
        [.. ModuleEdges(graph, config).Where(e => e.From.Kind == ModuleKind.Feature).GroupBy(e => e.From.Name, StringComparer.Ordinal)
            .Select(g => (Module: g.Key, Ce: g.Select(e => e.To.Name).Distinct(StringComparer.Ordinal).Count()))
            .Where(m => m.Ce > config.ModuleFanOutMax)
            .Select(m => new RuleViolation(ModuleRuleIds.ModuleFanOut, m.Module, m.Ce, $"Module {m.Module} depends on {m.Ce} modules (max {config.ModuleFanOutMax})"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];

    /// <summary>Orchestrator classes stay thin: bounded method count and bounded distinct dependencies.</summary>
    public static IReadOnlyList<RuleViolation> OrchestrationThin(TypeGraph graph, ModulesConfig config)
    {
        config.EnsureValid();
        var pattern = new Regex(config.OrchestratorPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var found = new List<RuleViolation>();
        foreach (var type in graph.Types.Where(t => t is Class && pattern.IsMatch(t.FullName)))
        {
            var methods = type.Members.OfType<MethodMember>().Count(m => m.MethodForm == MethodForm.Normal && !m.IsCompilerGenerated);
            if (methods > config.OrchestratorMaxMethods)
            {
                found.Add(new RuleViolation(ModuleRuleIds.OrchestrationThin, type.FullName + ":methods", methods, $"Orchestrator {type.FullName} has {methods} methods (max {config.OrchestratorMaxMethods})"));
            }

            var fanOut = graph.DependenciesOf(type).Count;
            if (fanOut > config.OrchestratorMaxFanOut)
            {
                found.Add(new RuleViolation(ModuleRuleIds.OrchestrationThin, type.FullName + ":fan-out", fanOut, $"Orchestrator {type.FullName} depends on {fanOut} types (max {config.OrchestratorMaxFanOut})"));
            }
        }

        return [.. found.OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>Rule ids that actually run for this config (the caps only when a limit is set).</summary>
    public static IReadOnlyList<string> ActiveRules(ModulesConfig config) =>
    [
        ModuleRuleIds.FeatureIsolation, ModuleRuleIds.SharedPurity, ModuleRuleIds.ModelIsLeaf, ModuleRuleIds.ModuleFanOut, ModuleRuleIds.OrchestrationThin,
        .. config.SharedMaxTypes is null ? [] : new[] { ModuleRuleIds.SharedCap },
        .. config.CompositionMaxTypes is null ? [] : new[] { ModuleRuleIds.CompositionCap },
    ];

    private static IReadOnlyList<RuleViolation> Cap(TypeGraph graph, ModulesConfig config, ModuleKind kind, int? max, string rule)
    {
        config.EnsureValid();
        return max is not { } limit
            ? []
            : [.. graph.Types.Where(t => !t.FullName.Contains('+', StringComparison.Ordinal)).Select(config.ModuleOf).OfType<ModuleSpec>().Where(m => m.Kind == kind).GroupBy(m => m.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > limit)
                .Select(g => new RuleViolation(rule, g.Key, g.Count(), $"Module {g.Key} has {g.Count()} types (max {limit})"))
                .OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<RuleViolation> Edges(TypeGraph graph, ModulesConfig config, string rule, Func<ModuleSpec, ModuleSpec, bool> forbidden, Func<string, string, string> message) =>
        [.. ModuleEdges(graph, config).Where(e => forbidden(e.From, e.To))
            .GroupBy(e => (From: e.From.Name, To: e.To.Name))
            .Select(g => new RuleViolation(rule, $"{g.Key.From}->{g.Key.To}", g.Count(), message(g.Key.From, g.Key.To)))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];

    /// <summary>One entry per type edge between two different modules.</summary>
    private static IEnumerable<(ModuleSpec From, ModuleSpec To)> ModuleEdges(TypeGraph graph, ModulesConfig config)
    {
        config.EnsureValid();
        foreach (var (from, to) in graph.Edges)
        {
            if (config.ModuleOf(from) is { } source && config.ModuleOf(to) is { } target && source.Name != target.Name)
            {
                yield return (source, target);
            }
        }
    }
}
