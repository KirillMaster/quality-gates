using System.Reflection;
using System.Text.Json;
using ArchUnitNET.Domain;
using Assembly = System.Reflection.Assembly;

namespace KirillMaster.QualityGates.Architecture;

/// <summary>Known type cycles (member sets, sorted) that are tolerated; anything not listed fails.</summary>
public sealed class TypeCycleBaseline
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, WriteIndented = true };

    public List<List<string>> Cycles
    {
        get;
        set => field = value ?? [];
    } = [];

    public static TypeCycleBaseline FromJson(string json) =>
        JsonSerializer.Deserialize<TypeCycleBaseline>(json, JsonOptions)
        ?? throw new InvalidOperationException("Type cycle baseline is empty.");

    /// <summary>Missing file means no cycles are tolerated.</summary>
    public static TypeCycleBaseline FromFile(string path) => File.Exists(path) ? FromJson(File.ReadAllText(path)) : new TypeCycleBaseline();

    public static TypeCycleBaseline From(IEnumerable<IReadOnlyList<string>> cycles) =>
        new() { Cycles = [.. cycles.Select(c => c.Order(StringComparer.Ordinal).ToList()).OrderBy(Key, StringComparer.Ordinal)] };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public bool Contains(IEnumerable<string> members) => Cycles.Any(c => Key(c) == Key(members));

    internal static string Key(IEnumerable<string> members) => string.Join('|', members.Order(StringComparer.Ordinal));
}

/// <summary>Detects dependency cycles between types as strongly connected components of the type graph.</summary>
public static class TypeCycles
{
    /// <summary>Cycles between types of the given assemblies; each cycle is the sorted list of member type names.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Find(ArchUnitNET.Domain.Architecture architecture, IEnumerable<Assembly> assemblies)
    {
        var names = assemblies.Select(a => a.GetName().Name).ToHashSet(StringComparer.Ordinal);
        var inScope = architecture.Types.Where(t => names.Contains(t.Assembly.Name)).ToArray();
        var nodes = inScope.Select(t => Owner(t.FullName)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var edges = nodes.ToDictionary(n => n, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var type in inScope)
        {
            var source = Owner(type.FullName);
            if (source is null)
            {
                continue;
            }

            foreach (var target in type.Dependencies.Select(d => Owner(d.Target.FullName)))
            {
                if (target is not null && target != source && nodes.Contains(target) && !IsEnclosing(target, source))
                {
                    edges[source].Add(target);
                }
            }
        }

        return [.. StronglyConnected(edges.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal)).Where(c => c.Count > 1).OrderBy(c => c[0], StringComparer.Ordinal)];
    }

    /// <summary>Cycles that are not in the baseline.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> New(IEnumerable<IReadOnlyList<string>> cycles, TypeCycleBaseline baseline) =>
        [.. cycles.Where(c => !baseline.Contains(c))];

    /// <summary>The type itself, or for compiler-generated types (state machines, closures) the nearest declaring type; null when there is none.</summary>
    internal static string? Owner(string fullName)
    {
        var generated = fullName.IndexOf('<', StringComparison.Ordinal);
        if (generated < 0)
        {
            return fullName;
        }

        var plus = fullName.LastIndexOf('+', generated);
        return plus < 0 ? null : fullName[..plus];
    }

    private static bool IsEnclosing(string candidate, string nested) => nested.StartsWith(candidate + "+", StringComparison.Ordinal);

    private static List<List<string>> StronglyConnected(Dictionary<string, string[]> edges)
    {
        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<List<string>>();

        void Visit(string node)
        {
            indices[node] = low[node] = index++;
            stack.Push(node);
            onStack.Add(node);
            foreach (var next in edges[node])
            {
                if (indices.TryGetValue(next, out var seen))
                {
                    if (onStack.Contains(next))
                    {
                        low[node] = Math.Min(low[node], seen);
                    }
                }
                else
                {
                    Visit(next);
                    low[node] = Math.Min(low[node], low[next]);
                }
            }

            if (low[node] == indices[node])
            {
                var component = new List<string>();
                string member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                }
                while (member != node);

                component.Sort(StringComparer.Ordinal);
                result.Add(component);
            }
        }

        foreach (var node in edges.Keys.Order(StringComparer.Ordinal).Where(n => !indices.ContainsKey(n)))
        {
            Visit(node);
        }

        return result;
    }
}
