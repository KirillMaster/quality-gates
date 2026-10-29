using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArchUnitNET.Domain;

namespace KirillMaster.QualityGates.Architecture.Hexagonal;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HexRole
{
    None,
    Core,
    PortsInput,
    PortsOutput,
    Adapters,
    Runners,
    Host,
}

/// <summary>Hexagonal layer roles derived from naming patterns (regex on namespace, then assembly name) plus the thresholds of the hexagonal rules.</summary>
public sealed class HexagonalProfile
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public string CorePattern { get; set; } = @"(^|\.)Core(\.|$)";

    public string PortsInputPattern { get; set; } = @"(^|\.)Ports\.Input(\.|$)";

    public string PortsOutputPattern { get; set; } = @"(^|\.)Ports\.Output(\.|$)";

    public string AdaptersPattern { get; set; } = @"(^|\.)Adapters(\.|$)";

    public string RunnersPattern { get; set; } = @"(^|\.)Runners(\.|$)";

    /// <summary>Composition root: may reference every role.</summary>
    public string HostPattern { get; set; } = @"(^|\.)Host(\.|$)";

    /// <summary>Group 1 names the adapter, for example Postgres in Shop.Adapters.Postgres.Repo; types without a match are shared adapter code.</summary>
    public string AdapterNamePattern { get; set; } = @"(?:^|\.)Adapters\.([^.]+)";

    public string Baseline { get; set; } = "quality/hexagonal-baseline.json";

    /// <summary>Roles an adapter may depend on besides adapters.</summary>
    public List<HexRole> AdaptersMayDependOn { get; set; } = [HexRole.Core, HexRole.PortsInput, HexRole.PortsOutput];

    /// <summary>Roles a runner may depend on besides runners; runners reach the core only through input ports.</summary>
    public List<HexRole> RunnersMayDependOn { get; set; } = [HexRole.PortsInput];

    /// <summary>Namespace prefixes of DI containers that Core and Ports must not use; System.IServiceProvider is always forbidden there.</summary>
    public List<string> ContainerNamespaces { get; set; } =
    [
        "Microsoft.Extensions.DependencyInjection", "Autofac", "Ninject", "StructureMap", "Lamar", "SimpleInjector", "Castle.Windsor", "Unity",
    ];

    /// <summary>Type name prefixes that must not appear in port signatures.</summary>
    public List<string> TechnologyTypes { get; set; } =
    [
        "System.Linq.IQueryable", "System.Net.Http.", "System.Text.Json.", "System.IO.Stream", "System.Data.", "Microsoft.EntityFrameworkCore.", "Amazon.", "Npgsql.",
    ];

    public int PortMaxMembers { get; set; } = 10;

    /// <summary>Share of identical member signatures (name and type) from which two types count as mirrors.</summary>
    public double MirrorSimilarity { get; set; } = 0.9;

    /// <summary>Types with fewer members are never mirrors.</summary>
    public int MirrorMinMembers { get; set; } = 2;

    public static HexagonalProfile FromJson(string json) =>
        JsonSerializer.Deserialize<HexagonalProfile>(json, JsonOptions)
        ?? throw new InvalidOperationException("Hexagonal profile is empty.");

    public static HexagonalProfile FromFile(string path) => FromJson(File.ReadAllText(path));

    private readonly ConcurrentDictionary<IType, HexRole> _roles = new();

    /// <summary>Role of a type (memoized per type, so set the patterns before the first rule runs): namespace patterns win over assembly-name patterns; within one source the order is Host, Runners, ports, Adapters, Core.</summary>
    public HexRole RoleOf(IType type) => _roles.GetOrAdd(type, Resolve);

    private HexRole Resolve(IType type)
    {
        var patterns = new (HexRole Role, string Pattern)[]
        {
            (HexRole.Host, HostPattern), (HexRole.Runners, RunnersPattern), (HexRole.PortsInput, PortsInputPattern),
            (HexRole.PortsOutput, PortsOutputPattern), (HexRole.Adapters, AdaptersPattern), (HexRole.Core, CorePattern),
        };
        foreach (var (role, pattern) in patterns)
        {
            if (TypeGraph.MatchesNamespace(pattern, type))
            {
                return role;
            }
        }

        foreach (var (role, pattern) in patterns)
        {
            if (TypeGraph.MatchesAssembly(pattern, type))
            {
                return role;
            }
        }

        return HexRole.None;
    }

    /// <summary>Adapter name of an adapter type, or null for shared adapter code.</summary>
    public string? AdapterOf(IType type)
    {
        var regex = TypeGraph.Compile(AdapterNamePattern);
        var match = regex.Match(type.Namespace.FullName);
        if (!match.Success)
        {
            match = regex.Match(type.Assembly.Name);
        }

        return match.Success ? match.Groups[1].Value : null;
    }
}
