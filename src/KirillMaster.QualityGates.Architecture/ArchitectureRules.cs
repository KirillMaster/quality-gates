using System.Reflection;
using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Slices;
using ArchUnitNET.Loader;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using static ArchUnitNET.Fluent.Slices.SliceRuleDefinition;
using Assembly = System.Reflection.Assembly;

namespace KirillMaster.QualityGates.Architecture;

/// <summary>Builds ArchUnitNET rules from an <see cref="ArchitectureConfig"/>.</summary>
public static class ArchitectureRules
{
    private static readonly Dictionary<string, ArchUnitNET.Domain.Architecture> Cache = new(StringComparer.Ordinal);

    public static ArchUnitNET.Domain.Architecture Load(IEnumerable<Assembly> assemblies)
    {
        var list = assemblies.Distinct().ToArray();
        var key = string.Join('|', list.Select(a => a.Location).Order(StringComparer.Ordinal));
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var architecture))
            {
                architecture = new ArchLoader().LoadAssemblies(list).Build();
                Cache[key] = architecture;
            }

            return architecture;
        }
    }

    public static IReadOnlyList<(string Name, IArchRule Rule)> LayerRules(ArchitectureConfig config)
    {
        var invalid = config.Validate().ToArray();
        if (invalid.Length > 0)
        {
            throw new InvalidOperationException(string.Join("; ", invalid));
        }

        var rules = new List<(string, IArchRule)>();
        foreach (var layer in config.Layers)
        {
            var allowed = layer.MayDependOn.ToHashSet(StringComparer.Ordinal);
            var forbidden = config.Layers.Where(o => o.Name != layer.Name && !allowed.Contains(o.Name)).Select(o => o.Pattern).ToArray();
            if (forbidden.Length > 0)
            {
                rules.Add(($"{layer.Name} layer depends only on [{string.Join(", ", layer.MayDependOn)}]",
                    Types().That().ResideInNamespaceMatching(layer.Pattern)
                        .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(Union(forbidden))
                        .Because($"layer {layer.Name} must not reach into other layers")));
            }
        }

        return rules;
    }

    public static IReadOnlyList<(string Name, IArchRule Rule)> ForbiddenNamespaceRules(ArchitectureConfig config) =>
        [.. config.Layers.Where(l => l.ForbiddenNamespaces.Count > 0).Select(l => ($"{l.Name} layer avoids [{string.Join(", ", l.ForbiddenNamespaces)}]",
            (IArchRule)Types().That().ResideInNamespaceMatching(l.Pattern)
                .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(Union(l.ForbiddenNamespaces.Select(n => "^" + Regex.Escape(n) + @"(\..*)?$")))
                .Because($"layer {l.Name} must stay free of infrastructure")))];

    public static IArchRule NamespaceCycleRule(ArchitectureConfig config)
    {
        return Slices().Matching(config.CycleSlicePattern)
            .Should().BeFreeOfCycles();
    }

    private static string Union(IEnumerable<string> patterns) => string.Join('|', patterns.Select(p => "(?:" + p + ")"));
}
