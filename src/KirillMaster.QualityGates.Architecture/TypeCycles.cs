using System.Reflection;
using System.Text.Json;
using ArchUnitNET.Domain;
using Assembly = System.Reflection.Assembly;

namespace KirillMaster.QualityGates.Architecture;

/// <summary>Known type cycles (member sets, sorted) that are tolerated; anything not listed fails.</summary>
public sealed class TypeCycleBaseline
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, WriteIndented = true };

    public List<List<string>> Cycles { get; set; } = [];

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
        var nodes = architecture.Types
            .Where(t => !IsCompilerGenerated(t.FullName) && names.Contains(t.Assembly.Name))
            .ToDictionary(t => t.FullName, StringComparer.Ordinal);
        var edges = nodes.ToDictionary(
            n => n.Key,
            n => n.Value.Dependencies
                .Select(d => d.Target.FullName)
                .Where(target => target != n.Key && nodes.ContainsKey(target) && !IsEnclosing(target, n.Key))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
        return [.. StronglyConnected(edges).Where(c => c.Count > 1).OrderBy(c => c[0], StringComparer.Ordinal)];
    }

    /// <summary>Cycles that are not in the baseline.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> New(IEnumerable<IReadOnlyList<string>> cycles, TypeCycleBaseline baseline) =>
        [.. cycles.Where(c => !baseline.Contains(c))];

    private static bool IsCompilerGenerated(string fullName) => fullName.Contains('<', StringComparison.Ordinal);

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
