using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using Assembly = System.Reflection.Assembly;

namespace KirillMaster.QualityGates.Architecture;

/// <summary>Type-level dependency graph of the given assemblies; compiler-generated types count towards their declaring type.</summary>
public sealed class TypeGraph
{
    private readonly Dictionary<IType, HashSet<IType>> _out;

    private TypeGraph(IReadOnlyList<IType> types, Dictionary<IType, HashSet<IType>> edges)
    {
        Types = types;
        _out = edges;
    }

    public IReadOnlyList<IType> Types { get; }

    public IEnumerable<(IType From, IType To)> Edges => _out.SelectMany(e => e.Value.Select(t => (e.Key, t)));

    public IReadOnlyCollection<IType> DependenciesOf(IType type) => _out.TryGetValue(type, out var set) ? set : [];

    public static TypeGraph Build(ArchUnitNET.Domain.Architecture architecture, IEnumerable<Assembly> assemblies)
    {
        var names = assemblies.Select(a => a.GetName().Name).ToHashSet(StringComparer.Ordinal);
        var inScope = architecture.Types.Where(t => names.Contains(t.Assembly.Name)).ToArray();
        var byName = new Dictionary<string, IType>(StringComparer.Ordinal);
        foreach (var type in inScope.Where(t => TypeCycles.Owner(t.FullName) == t.FullName))
        {
            byName[type.FullName] = type;
        }

        IType? Resolve(string fullName) => TypeCycles.Owner(fullName) is { } owner && byName.TryGetValue(owner, out var found) ? found : null;

        var edges = byName.Values.ToDictionary(t => t, _ => new HashSet<IType>());
        foreach (var type in inScope)
        {
            if (Resolve(type.FullName) is not { } source)
            {
                continue;
            }

            foreach (var target in type.Dependencies.Select(d => Resolve(d.Target.FullName)).OfType<IType>().Where(t => t != source))
            {
                edges[source].Add(target);
            }
        }

        return new TypeGraph([.. byName.Values.OrderBy(t => t.FullName, StringComparer.Ordinal)], edges);
    }

    private static readonly ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);

    /// <summary>Regex compiled once per pattern and reused.</summary>
    internal static Regex Compile(string pattern) =>
        Compiled.GetOrAdd(pattern, p => new Regex(p, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1)));

    /// <summary>Regex matched against the type's namespace and its assembly name.</summary>
    internal static bool Matches(string pattern, IType type) => MatchesNamespace(pattern, type) || MatchesAssembly(pattern, type);

    internal static bool MatchesNamespace(string pattern, IType type) => Compile(pattern).IsMatch(type.Namespace.FullName);

    internal static bool MatchesAssembly(string pattern, IType type) => Compile(pattern).IsMatch(type.Assembly.Name);
}
