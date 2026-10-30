namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConcurrencyAnalyzer : QgAnalyzer
{
    private static readonly ImmutableHashSet<string> MutableCollections = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "List", "Dictionary", "HashSet", "Queue", "Stack", "LinkedList", "SortedDictionary", "SortedSet", "SortedList",
        "ConcurrentDictionary", "ConcurrentBag", "ConcurrentQueue", "ConcurrentStack");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ReadabilityDescriptors.LockInCore, ReadabilityDescriptors.StaticMutableState);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var roles = new ShapeRoles(new CompilationConfig(start.Options, start.Compilation), start.Compilation);
            if (roles.Skip)
            {
                return;
            }

            start.RegisterSyntaxNodeAction(c => AnalyzeLock(c, roles), SyntaxKind.LockStatement);
            start.RegisterSymbolAction(c => AnalyzeField(c, roles), SymbolKind.Field);
        });

    private static void AnalyzeLock(SyntaxNodeAnalysisContext context, ShapeRoles roles)
    {
        var owner = context.ContainingSymbol?.ContainingType;
        if (owner is null ? !roles.CoreLayer : !roles.IsCore(owner))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.LockInCore, ((LockStatementSyntax)context.Node).LockKeyword.GetLocation()));
    }

    private static void AnalyzeField(SymbolAnalysisContext context, ShapeRoles roles)
    {
        var field = (IFieldSymbol)context.Symbol;
        var location = field.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null || !field.IsStatic || field.IsConst || field.IsImplicitlyDeclared || field.ContainingType.TypeKind == TypeKind.Enum
            || !roles.IsCore(field.ContainingType))
        {
            return;
        }

        if (!field.IsReadOnly)
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.StaticMutableState, location, field.Name, "not readonly"));
        }
        else if (IsMutableCollection(field.Type))
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.StaticMutableState, location, field.Name, "a mutable collection"));
        }
    }

    private static bool IsMutableCollection(ITypeSymbol type) =>
        type is INamedTypeSymbol { ContainingNamespace: { } ns } named
        && MutableCollections.Contains(named.Name)
        && ns.ToDisplayString().StartsWith("System.Collections", StringComparison.Ordinal)
        && !ns.ToDisplayString().Contains("Immutable");
}
