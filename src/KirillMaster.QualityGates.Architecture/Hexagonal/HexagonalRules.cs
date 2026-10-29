using ArchUnitNET.Domain;

namespace KirillMaster.QualityGates.Architecture.Hexagonal;

public static class HexagonalRuleIds
{
    public const string AdapterReferencesAdapter = "AdapterReferencesAdapter";
    public const string AdapterDirection = "AdapterDirection";
    public const string RunnerHostReferences = "RunnerHostReferences";
    public const string CoreNoContainer = "CoreNoContainer";
    public const string PortWidth = "PortWidth";
    public const string NoMirrorTypes = "NoMirrorTypes";
    public const string PortNoTechnologyTypes = "PortNoTechnologyTypes";
}

/// <summary>Hexagonal profile rules; each returns every violation, the caller applies the baseline.</summary>
public static class HexagonalRules
{
    private const string ServiceProvider = "System.IServiceProvider";

    public static IReadOnlyList<RuleViolation> All(TypeGraph graph, HexagonalProfile profile) =>
        [.. AdapterReferencesAdapter(graph, profile), .. AdapterDirection(graph, profile), .. RunnerHostReferences(graph, profile), .. CoreNoContainer(graph, profile),
            .. PortWidth(graph, profile), .. NoMirrorTypes(graph, profile), .. PortNoTechnologyTypes(graph, profile)];

    /// <summary>Two named adapters must not reference each other; share code through a port or Core.</summary>
    public static IReadOnlyList<RuleViolation> AdapterReferencesAdapter(TypeGraph graph, HexagonalProfile profile) =>
        [.. graph.Edges.Where(e => profile.RoleOf(e.From) == HexRole.Adapters && profile.RoleOf(e.To) == HexRole.Adapters)
            .Select(e => (From: profile.AdapterOf(e.From), To: profile.AdapterOf(e.To)))
            .Where(e => e.From is not null && e.To is not null && e.From != e.To)
            .GroupBy(e => $"{e.From}->{e.To}", StringComparer.Ordinal)
            .Select(g => new RuleViolation(HexagonalRuleIds.AdapterReferencesAdapter, g.Key, g.Count(), $"Adapter {g.Key.Replace("->", " depends on adapter ", StringComparison.Ordinal)}"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];

    /// <summary>Dependencies point inwards: Core and ports never reach adapters, runners or host; adapters only reach the configured roles.</summary>
    public static IReadOnlyList<RuleViolation> AdapterDirection(TypeGraph graph, HexagonalProfile profile)
    {
        HexRole[] inner = [HexRole.Core, HexRole.PortsInput, HexRole.PortsOutput];
        return [.. graph.Edges.Select(e => (e.From, e.To, FromRole: profile.RoleOf(e.From), ToRole: profile.RoleOf(e.To)))
            .Where(e => e.ToRole != HexRole.None && e.FromRole switch
            {
                HexRole.Core or HexRole.PortsInput or HexRole.PortsOutput => !inner.Contains(e.ToRole),
                HexRole.Adapters => e.ToRole != HexRole.Adapters && !profile.AdaptersMayDependOn.Contains(e.ToRole),
                _ => false,
            })
            .Select(e => new RuleViolation(HexagonalRuleIds.AdapterDirection, $"{e.From.FullName}->{e.To.FullName}", 1, $"{e.FromRole} type {e.From.FullName} must not depend on {e.ToRole} type {e.To.FullName}"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>Runners talk to the core only through the configured roles (input ports by default); no level skipping.</summary>
    public static IReadOnlyList<RuleViolation> RunnerHostReferences(TypeGraph graph, HexagonalProfile profile) =>
        [.. graph.Edges.Select(e => (e.From, e.To, ToRole: profile.RoleOf(e.To)))
            .Where(e => profile.RoleOf(e.From) == HexRole.Runners && e.ToRole != HexRole.None && e.ToRole != HexRole.Runners && !profile.RunnersMayDependOn.Contains(e.ToRole))
            .Select(e => new RuleViolation(HexagonalRuleIds.RunnerHostReferences, $"{e.From.FullName}->{e.To.FullName}", 1, $"Runner {e.From.FullName} must not depend on {e.ToRole} type {e.To.FullName}"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];

    /// <summary>Core and ports contain no DI container types (service locator, registration APIs, IServiceProvider).</summary>
    public static IReadOnlyList<RuleViolation> CoreNoContainer(TypeGraph graph, HexagonalProfile profile) =>
        [.. graph.Types.Where(t => profile.RoleOf(t) is HexRole.Core or HexRole.PortsInput or HexRole.PortsOutput)
            .SelectMany(t => t.Dependencies.Select(d => d.Target.FullName).Where(name => IsContainer(name, profile)).Distinct(StringComparer.Ordinal)
                .Select(name => new RuleViolation(HexagonalRuleIds.CoreNoContainer, $"{t.FullName}->{name}", 1, $"{t.FullName} must not use DI container type {name}")))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];

    /// <summary>A port interface has at most <see cref="HexagonalProfile.PortMaxMembers"/> members.</summary>
    public static IReadOnlyList<RuleViolation> PortWidth(TypeGraph graph, HexagonalProfile profile) =>
        [.. Ports(graph, profile).Select(t => (Type: t, Members: t.Members.Count(m => m is PropertyMember || m is MethodMember { MethodForm: MethodForm.Normal })))
            .Where(p => p.Members > profile.PortMaxMembers)
            .Select(p => new RuleViolation(HexagonalRuleIds.PortWidth, p.Type.FullName, p.Members, $"Port {p.Type.FullName} has {p.Members} members (max {profile.PortMaxMembers})"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];

    /// <summary>Types of Ports.Input and Ports.Output with (nearly) identical member signatures; merge them into a shared contracts module.</summary>
    public static IReadOnlyList<RuleViolation> NoMirrorTypes(TypeGraph graph, HexagonalProfile profile)
    {
        var inputs = Shapes(graph, profile, HexRole.PortsInput);
        var outputs = Shapes(graph, profile, HexRole.PortsOutput);
        return [.. inputs.SelectMany(i => outputs.Select(o => (In: i, Out: o)))
            .Where(p => Similarity(p.In.Members, p.Out.Members) >= profile.MirrorSimilarity)
            .Select(p => new RuleViolation(HexagonalRuleIds.NoMirrorTypes, $"{p.In.Type.FullName}<->{p.Out.Type.FullName}", 1, $"{p.In.Type.FullName} mirrors {p.Out.Type.FullName}"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>Port signatures do not leak technology types (queryables, HTTP, streams, ORM, cloud SDK).</summary>
    public static IReadOnlyList<RuleViolation> PortNoTechnologyTypes(TypeGraph graph, HexagonalProfile profile) =>
        [.. Ports(graph, profile).SelectMany(t => t.Dependencies.Select(d => d.Target.FullName)
                .Where(name => profile.TechnologyTypes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))).Distinct(StringComparer.Ordinal)
                .Select(name => new RuleViolation(HexagonalRuleIds.PortNoTechnologyTypes, $"{t.FullName}->{name}", 1, $"Port {t.FullName} exposes technology type {name}")))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];

    private static IEnumerable<IType> Ports(TypeGraph graph, HexagonalProfile profile) =>
        graph.Types.Where(t => t is Interface && profile.RoleOf(t) is HexRole.PortsInput or HexRole.PortsOutput);

    private static bool IsContainer(string name, HexagonalProfile profile) =>
        name == ServiceProvider || profile.ContainerNamespaces.Any(n => name.StartsWith(n + ".", StringComparison.Ordinal));

    /// <summary>Ids of the rules the profile runs.</summary>
    public static IReadOnlyList<string> ActiveRules { get; } =
    [
        HexagonalRuleIds.AdapterReferencesAdapter, HexagonalRuleIds.AdapterDirection, HexagonalRuleIds.RunnerHostReferences, HexagonalRuleIds.CoreNoContainer,
        HexagonalRuleIds.PortWidth, HexagonalRuleIds.NoMirrorTypes, HexagonalRuleIds.PortNoTechnologyTypes,
    ];

    private static List<(IType Type, HashSet<string> Members)> Shapes(TypeGraph graph, HexagonalProfile profile, HexRole role) =>
        [.. graph.Types.Where(t => t is not Interface && profile.RoleOf(t) == role)
            .Select(t => (Type: t, Members: t.Members.OfType<PropertyMember>().Where(IsDeclared).Select(p => p.Name + ":" + p.Type.FullName)
                .Concat(t.Members.OfType<FieldMember>().Where(IsDeclared).Select(f => f.Name + ":" + f.Type.FullName)).ToHashSet(StringComparer.Ordinal)))
            .Where(s => s.Members.Count >= profile.MirrorMinMembers)];

    /// <summary>Members the author wrote: not compiler-generated and not the record EqualityContract.</summary>
    private static bool IsDeclared(IMember member) => !member.IsCompilerGenerated && member.Name != "EqualityContract";

    private static double Similarity(HashSet<string> a, HashSet<string> b) => (double)a.Intersect(b).Count() / Math.Max(a.Count, b.Count);
}
