using ArchUnitNET.Domain;

namespace KirillMaster.QualityGates.Architecture.Hexagonal;

/// <summary>Optional use-case-slice rules of the hexagonal profile (Runner, Handler, Domain, ports, Adapter); enabled through <see cref="HexagonalProfile.ExtraRules"/>.</summary>
public static class FlowRules
{
    private enum Level
    {
        None,
        Runner,
        Handler,
        Domain,
        Port,
        Adapter,
    }

    /// <summary>Nothing points back up the flow: Domain, ports and adapters never use a concrete handler; handlers, Domain, ports and adapters never use runners.</summary>
    public static IReadOnlyList<RuleViolation> FlowDirection(TypeGraph graph, HexagonalProfile profile)
    {
        var handlers = HandlerNames(graph, profile);
        return [.. graph.Edges.Select(e => (e.From, e.To, FromLevel: LevelOf(e.From, profile, handlers), ToLevel: LevelOf(e.To, profile, handlers)))
            .Where(e => (e.ToLevel == Level.Handler && e.FromLevel is Level.Domain or Level.Port or Level.Adapter)
                || (e.ToLevel == Level.Runner && e.FromLevel is Level.Handler or Level.Domain or Level.Port or Level.Adapter))
            .Select(e => new RuleViolation(HexagonalRuleIds.FlowDirection, $"{e.From.FullName}->{e.To.FullName}", 1, $"{e.FromLevel} type {e.From.FullName} points back up the flow to {e.ToLevel} type {e.To.FullName}"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>No level skipping (L5): runners reach the core only through handlers, never output ports or adapters; handlers, Domain and ports never use an adapter, only through an output port.</summary>
    public static IReadOnlyList<RuleViolation> NoLevelSkipping(TypeGraph graph, HexagonalProfile profile)
    {
        var handlers = HandlerNames(graph, profile);
        return [.. graph.Edges.Select(e => (e.From, e.To, FromLevel: LevelOf(e.From, profile, handlers), ToLevel: LevelOf(e.To, profile, handlers)))
            .Where(e => (e.FromLevel == Level.Runner && (e.ToLevel == Level.Adapter || (e.To is Interface && profile.RoleOf(e.To) == HexRole.PortsOutput)))
                || (e.FromLevel is Level.Handler or Level.Domain or Level.Port && e.ToLevel == Level.Adapter))
            .Select(e => new RuleViolation(HexagonalRuleIds.NoLevelSkipping, $"{e.From.FullName}->{e.To.FullName}", 1, $"{e.FromLevel} type {e.From.FullName} skips a level to reach {e.ToLevel} type {e.To.FullName}"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>A handler uses no other handler: neither its class nor an input port of another use case.</summary>
    public static IReadOnlyList<RuleViolation> HandlerIsolation(TypeGraph graph, HexagonalProfile profile)
    {
        var handlers = HandlerNames(graph, profile);
        var ports = HandlerPorts(graph, profile).Select(p => p.FullName).ToHashSet(StringComparer.Ordinal);
        return [.. graph.Types.Where(t => handlers.Contains(t.FullName))
            .SelectMany(t => graph.DependenciesOf(t).Where(d => d != t && (handlers.Contains(d.FullName) || ports.Contains(d.FullName)) && !OwnPorts(t, ports).Contains(d.FullName))
                .Select(d => new RuleViolation(HexagonalRuleIds.HandlerIsolation, $"{t.FullName}->{d.FullName}", 1, $"Handler {t.FullName} must not depend on handler {d.FullName}")))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>A Result is only returned: no property, field or parameter of Result type outside <see cref="HexagonalProfile.ResultAllowedRoles"/>.</summary>
    public static IReadOnlyList<RuleViolation> ResultAtBoundary(TypeGraph graph, HexagonalProfile profile)
    {
        var result = TypeGraph.Compile(profile.ResultTypePattern);
        return [.. graph.Types.Where(t => !IsTest(t, profile) && !profile.ResultAllowedRoles.Contains(profile.RoleOf(t)))
            .SelectMany(t => HeldTypes(t).Where(h => result.IsMatch(h.Type.FullName)).Select(h => $"{t.FullName}.{h.Member}"))
            .GroupBy(key => key, StringComparer.Ordinal)
            .Select(g => new RuleViolation(HexagonalRuleIds.ResultAtBoundary, g.Key, g.Count(), $"{g.Key} stores or takes a Result; return it instead"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>One use case, one port: a handler port has exactly one member, one implementation (decorators of the same port excepted) and a handler class implements one port and exposes no public method beyond it.</summary>
    public static IReadOnlyList<RuleViolation> OnePortPerUseCase(TypeGraph graph, HexagonalProfile profile)
    {
        var ports = HandlerPorts(graph, profile).ToArray();
        var portNames = ports.Select(p => p.FullName).ToHashSet(StringComparer.Ordinal);
        var handlers = graph.Types.Where(t => IsHandlerClass(t, portNames, profile)).ToArray();
        var found = new List<RuleViolation>();
        foreach (var port in ports)
        {
            var members = Members(port);
            if (members != 1)
            {
                found.Add(new RuleViolation(HexagonalRuleIds.OnePortPerUseCase, port.FullName + ":members", members, $"Handler port {port.FullName} has {members} members (exactly 1)"));
            }

            var implementations = handlers.Count(h => OwnPorts(h, portNames).Contains(port.FullName) && !HeldTypes(h).Any(x => x.Type.FullName == port.FullName));
            if (implementations > 1)
            {
                found.Add(new RuleViolation(HexagonalRuleIds.OnePortPerUseCase, port.FullName + ":implementations", implementations, $"Handler port {port.FullName} has {implementations} implementations (decorators excepted)"));
            }
        }

        foreach (var handler in handlers)
        {
            var own = OwnPorts(handler, portNames).ToArray();
            if (own.Length > 1)
            {
                found.Add(new RuleViolation(HexagonalRuleIds.OnePortPerUseCase, handler.FullName + ":ports", own.Length, $"Handler {handler.FullName} implements {own.Length} use-case ports (exactly 1)"));
            }

            var extra = handler.Members.OfType<MethodMember>().Count(m => m.MethodForm == MethodForm.Normal && !m.IsCompilerGenerated && m.Visibility == Visibility.Public) - own.Sum(p => Members(ports.First(x => x.FullName == p)));
            if (extra > 0)
            {
                found.Add(new RuleViolation(HexagonalRuleIds.OnePortPerUseCase, handler.FullName + ":public-methods", extra, $"Handler {handler.FullName} has {extra} public methods beyond its port"));
            }
        }

        return [.. found.OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>Folders under Core, ports, adapters and runners are features of the registry or words of the closed vocabulary; the forbidden names (Services, Helpers, Utils, ...) never are.</summary>
    public static IReadOnlyList<RuleViolation> FolderAllowlist(TypeGraph graph, HexagonalProfile profile)
    {
        var pattern = TypeGraph.Compile(profile.FeatureFolderPattern);
        return [.. graph.Types.Select(t => (Type: t, Match: pattern.Match(t.Namespace.FullName)))
            .Where(t => t.Match.Success)
            .Select(t => (Folder: t.Match.Groups[1].Value, Path: t.Type.Namespace.FullName[..(t.Match.Groups[1].Index + t.Match.Groups[1].Length)]))
            .Where(t => profile.ForbiddenFolders.Contains(t.Folder, StringComparer.Ordinal)
                || (profile.Features.Count > 0 && !profile.Features.Contains(t.Folder, StringComparer.Ordinal) && !profile.FolderVocabulary.Contains(t.Folder, StringComparer.Ordinal)))
            .GroupBy(t => t.Path, StringComparer.Ordinal)
            .Select(g => new RuleViolation(HexagonalRuleIds.FolderAllowlist, g.Key, g.Count(), $"Folder {g.Key} is not a registered feature or allowed folder"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>Every feature of Ports.Input and Ports.Output has a Core feature, and every Core feature has an input port feature.</summary>
    public static IReadOnlyList<RuleViolation> FeatureMirror(TypeGraph graph, HexagonalProfile profile)
    {
        var core = Features(graph, profile.CoreFeaturePattern, t => profile.RoleOf(t) == HexRole.Core);
        if (core.Count == 0)
        {
            return [];
        }

        var input = Features(graph, profile.PortFeaturePattern, t => profile.RoleOf(t) == HexRole.PortsInput);
        var output = Features(graph, profile.PortFeaturePattern, t => profile.RoleOf(t) == HexRole.PortsOutput);
        return
        [
            .. input.Concat(output).Distinct(StringComparer.Ordinal).Where(f => !core.Contains(f))
                .Select(f => new RuleViolation(HexagonalRuleIds.FeatureMirror, "Ports:" + f, 1, $"Ports feature {f} has no Core feature"))
                .Concat(core.Where(f => !input.Contains(f)).Select(f => new RuleViolation(HexagonalRuleIds.FeatureMirror, "Core:" + f, 1, $"Core feature {f} has no input port feature")))
                .OrderBy(v => v.Key, StringComparer.Ordinal),
        ];
    }

    /// <summary>Z2: every handler class or its use-case port is referenced from test code (<see cref="HexagonalProfile.TestPattern"/>); include the test assemblies in the analysed set.</summary>
    public static IReadOnlyList<RuleViolation> HandlerReferenced(TypeGraph graph, HexagonalProfile profile)
    {
        var portNames = HandlerPorts(graph, profile).Select(p => p.FullName).ToHashSet(StringComparer.Ordinal);
        var tests = graph.Types.Where(t => IsTest(t, profile)).ToHashSet();
        var referenced = tests.SelectMany(graph.DependenciesOf).Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
        return [.. graph.Types.Where(t => IsHandlerClass(t, portNames, profile))
            .Where(t => !referenced.Contains(t.FullName) && !OwnPorts(t, portNames).Any(referenced.Contains))
            .Select(t => new RuleViolation(HexagonalRuleIds.HandlerReferenced, t.FullName, 1, $"Handler {t.FullName} is not referenced by any test"))
            .OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    private static HashSet<string> Features(TypeGraph graph, string pattern, Func<IType, bool> inRole)
    {
        var regex = TypeGraph.Compile(pattern);
        return [.. graph.Types.Where(inRole).Select(t => regex.Match(t.Namespace.FullName)).Where(m => m.Success).Select(m => m.Groups[1].Value)];
    }

    private static IEnumerable<IType> HandlerPorts(TypeGraph graph, HexagonalProfile profile)
    {
        var name = TypeGraph.Compile(profile.HandlerInterfacePattern);
        return graph.Types.Where(t => t is Interface && profile.RoleOf(t) == HexRole.PortsInput && name.IsMatch(t.Name));
    }

    private static HashSet<string> HandlerNames(TypeGraph graph, HexagonalProfile profile)
    {
        var ports = HandlerPorts(graph, profile).Select(p => p.FullName).ToHashSet(StringComparer.Ordinal);
        return [.. graph.Types.Where(t => IsHandlerClass(t, ports, profile)).Select(t => t.FullName)];
    }

    private static bool IsHandlerClass(IType type, HashSet<string> ports, HexagonalProfile profile) => type is Class { IsAbstract: false } && !IsTest(type, profile) && OwnPorts(type, ports).Any();

    private static bool IsTest(IType type, HexagonalProfile profile) => TypeGraph.Matches(profile.TestPattern, type);

    private static IEnumerable<string> OwnPorts(IType type, HashSet<string> ports) => type.ImplementedInterfaces.Select(i => i.FullName).Where(ports.Contains);

    private static int Members(IType type) => type.Members.Count(m => m is PropertyMember || m is MethodMember { MethodForm: MethodForm.Normal });

    private static IEnumerable<(string Member, IType Type)> HeldTypes(IType type) =>
        type.Members.Where(m => !m.IsCompilerGenerated).SelectMany(m => m switch
        {
            PropertyMember p => [(p.Name, p.Type)],
            FieldMember f => [(f.Name, f.Type)],
            MethodMember { MethodForm: MethodForm.Normal or MethodForm.Constructor } method => method.Parameters.Select((p, i) => ($"{method.Name}({string.Join(',', method.Parameters.Select(x => x.FullName))})#{i}", p)),
            _ => [],
        });

    private static Level LevelOf(IType type, HexagonalProfile profile, HashSet<string> handlers) =>
        TypeGraph.Compile(profile.CompositionTypePattern).IsMatch(type.Name) ? Level.None : handlers.Contains(type.FullName) ? Level.Handler : profile.RoleOf(type) switch
        {
            HexRole.Runners => Level.Runner,
            HexRole.Core => Level.Domain,
            HexRole.PortsInput or HexRole.PortsOutput => Level.Port,
            HexRole.Adapters => Level.Adapter,
            _ => Level.None,
        };

    /// <summary>Rules selected by <see cref="HexagonalProfile.ExtraRules"/>.</summary>
    internal static IReadOnlyList<RuleViolation> Selected(TypeGraph graph, HexagonalProfile profile)
    {
        var extra = profile.ExtraRules.ToHashSet(StringComparer.Ordinal);
        var found = new List<RuleViolation>();
        foreach (var (id, rule) in Rules)
        {
            if (extra.Contains(id))
            {
                found.AddRange(rule(graph, profile));
            }
        }

        return found;
    }

    /// <summary>A selected rule whose roles match no type is a config error (wrong patterns), not a silent pass.</summary>
    internal static void EnsureRolesMatch(TypeGraph graph, HexagonalProfile profile)
    {
        var roles = graph.Types.Select(profile.RoleOf).ToHashSet();
        var missing = profile.ExtraRules.Distinct(StringComparer.Ordinal).SelectMany(rule => Needs(rule).Where(role => !roles.Contains(role)).Select(role => $"{rule} needs a {role} type"))
            .Concat(profile.ExtraRules.Contains(HexagonalRuleIds.HandlerReferenced) && !graph.Types.Any(t => IsTest(t, profile)) ? [$"{HexagonalRuleIds.HandlerReferenced} needs test types (TestPattern)"] : [])
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException("Hexagonal role patterns match nothing: " + string.Join("; ", missing) + ". Check the profile patterns and the analysed assemblies.");
        }
    }

    private static HexRole[] Needs(string rule) =>
        rule switch
        {
            HexagonalRuleIds.HandlerIsolation or HexagonalRuleIds.OnePortPerUseCase or HexagonalRuleIds.HandlerReferenced => [HexRole.PortsInput],
            HexagonalRuleIds.FeatureMirror => [HexRole.Core, HexRole.PortsInput],
            HexagonalRuleIds.FlowDirection or HexagonalRuleIds.NoLevelSkipping => [HexRole.Core],
            HexagonalRuleIds.ResultAtBoundary => [HexRole.Core],
            _ => [],
        };

    internal static readonly (string Id, Func<TypeGraph, HexagonalProfile, IReadOnlyList<RuleViolation>> Run)[] Rules =
    [
        (HexagonalRuleIds.FlowDirection, FlowDirection), (HexagonalRuleIds.NoLevelSkipping, NoLevelSkipping), (HexagonalRuleIds.HandlerIsolation, HandlerIsolation),
        (HexagonalRuleIds.ResultAtBoundary, ResultAtBoundary), (HexagonalRuleIds.OnePortPerUseCase, OnePortPerUseCase), (HexagonalRuleIds.FolderAllowlist, FolderAllowlist),
        (HexagonalRuleIds.FeatureMirror, FeatureMirror), (HexagonalRuleIds.HandlerReferenced, HandlerReferenced),
    ];

    /// <summary>Unknown names in <see cref="HexagonalProfile.ExtraRules"/> are a config error, not a silent no-op.</summary>
    internal static void EnsureKnown(HexagonalProfile profile)
    {
        var unknown = profile.ExtraRules.Where(r => !Rules.Any(k => k.Id == r)).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException("Unknown hexagonal rule(s) in ExtraRules: " + string.Join(", ", unknown));
        }
    }
}
