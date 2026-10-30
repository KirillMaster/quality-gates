namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PortShapeAnalyzer : QgAnalyzer
{
    private static readonly ImmutableHashSet<string> Primitives = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "System.String", "System.Int32", "System.Int64", "System.Int16", "System.UInt32", "System.UInt64", "System.Guid");

    private static readonly ImmutableHashSet<string> MoneyPrimitives = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "System.Decimal", "System.Double", "System.Single");

    private static readonly ImmutableHashSet<string> MutableCollections = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "System.Collections.Generic.List", "System.Collections.Generic.Dictionary", "System.Collections.Generic.HashSet",
        "System.Collections.Generic.SortedSet", "System.Collections.Generic.SortedDictionary", "System.Collections.Generic.LinkedList",
        "System.Collections.Generic.Queue", "System.Collections.Generic.Stack", "System.Collections.Generic.IList",
        "System.Collections.Generic.ICollection", "System.Collections.Generic.IDictionary", "System.Collections.Generic.ISet",
        "System.Collections.ObjectModel.Collection", "System.Collections.ObjectModel.ObservableCollection");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ShapeDescriptors.PrimitiveInPort, ShapeDescriptors.LeakyPort, ShapeDescriptors.PortWithoutCancellation,
        ShapeDescriptors.TechnologyTypeInPort, ShapeDescriptors.PortTypeNotWhitelisted);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var config = new CompilationConfig(start.Options, start.Compilation);
            var roles = new ShapeRoles(config, start.Compilation);
            if (!roles.Skip)
            {
                start.RegisterSymbolAction(c => AnalyzePort(c, roles), SymbolKind.NamedType);
            }
        });

    private static void AnalyzePort(SymbolAnalysisContext context, ShapeRoles roles)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!roles.IsPortInterface(type))
        {
            return;
        }

        var tree = type.Locations.First(l => l.IsInSource).SourceTree!;
        var idPattern = Patterns.Option(context.Options, tree, "qg_id_name_pattern", ShapeDescriptors.IdNamePattern);
        var moneyPattern = Patterns.Option(context.Options, tree, "qg_money_name_pattern", ShapeDescriptors.MoneyNamePattern);
        var technology = roles.List(type, "qg_port_technology_prefixes", ShapeDescriptors.PortTechnologyPrefixes);
        var whitelist = roles.List(type, "qg_port_type_whitelist", ShapeDescriptors.PortWhitelist);
        var allowedAssemblies = roles.List(type, "qg_port_allowed_assemblies", ShapeDescriptors.PortAllowedAssemblies);
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
        {
            var location = method.Locations[0];
            if (IsAsync(method.ReturnType) && method.Name != "DisposeAsync" && !method.Parameters.Any(p => TypeNames.IsCancellationToken(p.Type)))
            {
                context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.PortWithoutCancellation, location, method.Name));
            }

            foreach (var parameter in method.Parameters)
            {
                CheckParameter(context, method, parameter, idPattern, moneyPattern);
            }

            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var uses = method.Parameters.Select(p => (Type: p.Type, IsReturn: false)).Append((method.ReturnType, true));
            foreach (var (used, isReturn) in uses)
            {
                foreach (var part in TypeNames.Flatten(used))
                {
                    var (descriptor, name) = Classify(part, isReturn, context.Compilation, technology, whitelist, allowedAssemblies);
                    if (descriptor is not null && seen.Add(descriptor.Id + name))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(descriptor, location, method.Name, name));
                    }
                }
            }
        }
    }

    private static void CheckParameter(SymbolAnalysisContext context, IMethodSymbol method, IParameterSymbol parameter, string idPattern, string moneyPattern)
    {
        var element = TypeNames.Element(parameter.Type);
        var full = TypeNames.Full(element);
        var kind = Primitives.Contains(full) && Patterns.IsMatch(idPattern, parameter.Name) ? "id"
            : MoneyPrimitives.Contains(full) && Patterns.IsMatch(moneyPattern, parameter.Name) ? "amount"
            : null;
        if (kind is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.PrimitiveInPort, parameter.Locations[0], parameter.Name, method.Name, kind));
        }
    }

    private static bool IsAsync(ITypeSymbol type) => TypeNames.IsTaskLike(type) || TypeNames.IsAsyncEnumerable(type);

    private static (DiagnosticDescriptor? Descriptor, string Name) Classify(
        ITypeSymbol type, bool isReturn, Compilation compilation, ImmutableHashSet<string> technology, ImmutableHashSet<string> whitelist, ImmutableHashSet<string> allowedAssemblies)
    {
        if (type is ITypeParameterSymbol or IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        {
            return (null, string.Empty);
        }

        var full = TypeNames.Full(type);
        if (full == "System.Linq.IQueryable" || full.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || (isReturn && (type is IArrayTypeSymbol || MutableCollections.Contains(full))))
        {
            return (ShapeDescriptors.LeakyPort, type is IArrayTypeSymbol ? type.ToDisplayString() : full);
        }

        if (technology.Any(prefix => full == prefix || full.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)))
        {
            return (ShapeDescriptors.TechnologyTypeInPort, full);
        }

        var allowed = type is IArrayTypeSymbol
            || (type.SpecialType != SpecialType.None && type.SpecialType != SpecialType.System_Object)
            || type.TypeKind == TypeKind.Enum
            || SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
            || whitelist.Contains(full)
            || allowedAssemblies.Any(a => type.ContainingAssembly?.Name.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0);
        return allowed || type is IErrorTypeSymbol ? (null, string.Empty) : (ShapeDescriptors.PortTypeNotWhitelisted, full);
    }
}
