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

    public string RunnersPattern { get; set; } = @"(^|\.)Runners?(\.|$)";

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

    /// <summary>Optional rules (<see cref="HexagonalRuleIds"/>: FlowDirection, NoLevelSkipping, HandlerIsolation, ResultAtBoundary, OnePortPerUseCase, FolderAllowlist, FeatureMirror, HandlerReferenced) that run in addition to the base rules.</summary>
    public List<string> ExtraRules { get; set; } = [];

    /// <summary>Simple type name of a use-case port in Ports.Input; a handler is a non-abstract class implementing one.</summary>
    public string HandlerInterfacePattern { get; set; } = @"^I[A-Z]\w*Handler(`\d+)?$";

    /// <summary>Full type name of the Result type.</summary>
    public string ResultTypePattern { get; set; } = @"(^|\.)Result(`\d+)?$";

    /// <summary>Roles that may hold or take a Result as a property, field or parameter; everywhere else it is only returned.</summary>
    public List<HexRole> ResultAllowedRoles { get; set; } = [HexRole.Runners, HexRole.Host];

    /// <summary>Assembly or namespace regex of test code (HandlerReferenced); include the test assemblies in the analysed set.</summary>
    public string TestPattern { get; set; } = @"(^|\.)(Unit|Integration)?Tests?(\.|$)";

    /// <summary>Group 1 names the feature folder in the namespace of a type of Core, ports, adapters or runners (FolderAllowlist).</summary>
    public string FeatureFolderPattern { get; set; } = @"(?:^|\.)(?:Core\.(?:Features|Internal)|Ports\.(?:Input|Output)|Adapters\.[^.]+|Runners?\.[^.]+)\.([^.]+)";

    /// <summary>Simple type names of DI registration or composition classes; they wire handlers and adapters together and are outside the flow levels.</summary>
    public string CompositionTypePattern { get; set; } = @"(ServiceCollection\w*Extensions|ServiceRegistration\w*|DependencyInjection\w*|CompositionRoot\w*)$";

    /// <summary>Group 1 names the feature in a Core namespace (FeatureMirror), by default <c>Core.Features.X</c> or <c>Core.Internal.X</c>; no Core feature means the rule does nothing.</summary>
    public string CoreFeaturePattern { get; set; } = @"(?:^|\.)Core\.(?:Features|Internal)\.([^.]+)";

    /// <summary>Group 1 names the feature in a Ports.Input or Ports.Output namespace (FeatureMirror).</summary>
    public string PortFeaturePattern { get; set; } = @"(?:^|\.)Ports\.(?:Input|Output)\.([^.]+)";

    /// <summary>Feature registry of FolderAllowlist; empty checks only <see cref="ForbiddenFolders"/>.</summary>
    public List<string> Features { get; set; } = [];

    /// <summary>Closed vocabulary of non-feature folders that stay allowed next to <see cref="Features"/>.</summary>
    public List<string> FolderVocabulary { get; set; } = ["Model", "Events", "Errors", "Commands", "Queries"];

    /// <summary>Folder names that are never allowed (dumping grounds).</summary>
    public List<string> ForbiddenFolders { get; set; } =
    [
        "Services", "Helpers", "Utils", "Utilities", "Common", "Shared", "Mappers", "Dtos", "Contracts", "Interfaces", "Internal", "Infrastructure", "Base",
    ];

    public static HexagonalProfile FromJson(string json) =>
        JsonSerializer.Deserialize<HexagonalProfile>(json, JsonOptions)
        ?? throw new InvalidOperationException("Hexagonal profile is empty.");

    public static HexagonalProfile FromFile(string path) => FromJson(File.ReadAllText(path));

    private readonly ConcurrentDictionary<IType, HexRole> _roles = new();

    /// <summary>Role of a type (memoized per type, so set the patterns before the first rule runs); test types (<see cref="TestPattern"/>) have no role: namespace patterns win over assembly-name patterns; within one source the order is Host, Runners, ports, Adapters, Core.</summary>
    public HexRole RoleOf(IType type) => _roles.GetOrAdd(type, Resolve);

    private HexRole Resolve(IType type)
    {
        if (TypeGraph.Matches(TestPattern, type))
        {
            return HexRole.None;
        }

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
