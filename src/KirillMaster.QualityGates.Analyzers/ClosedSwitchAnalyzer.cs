using System.Collections.Generic;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ClosedSwitchAnalyzer : QgAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(ReadabilityDescriptors.ClosedSwitchDiscard);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var hierarchies = new ClosedHierarchies(start.Compilation);
            start.RegisterSyntaxNodeAction(c => AnalyzeExpression(c, hierarchies), SyntaxKind.SwitchExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeStatement(c, hierarchies), SyntaxKind.SwitchStatement);
        });

    private static void AnalyzeExpression(SyntaxNodeAnalysisContext context, ClosedHierarchies hierarchies)
    {
        var node = (SwitchExpressionSyntax)context.Node;
        var type = context.SemanticModel.GetTypeInfo(node.GoverningExpression, context.CancellationToken).Type;
        if (!hierarchies.IsClosed(type))
        {
            return;
        }

        foreach (var arm in node.Arms.Where(a => a.WhenClause is null && IsDiscard(a.Pattern) && a.Expression is not ThrowExpressionSyntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.ClosedSwitchDiscard, arm.Pattern.GetLocation(), type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    private static void AnalyzeStatement(SyntaxNodeAnalysisContext context, ClosedHierarchies hierarchies)
    {
        var node = (SwitchStatementSyntax)context.Node;
        var type = context.SemanticModel.GetTypeInfo(node.Expression, context.CancellationToken).Type;
        if (!hierarchies.IsClosed(type))
        {
            return;
        }

        foreach (var section in node.Sections.Where(s => !s.Statements.Any(st => st is ThrowStatementSyntax)))
        {
            foreach (var label in section.Labels.Where(IsDiscardLabel))
            {
                context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.ClosedSwitchDiscard, label.GetLocation(), type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }
        }
    }

    private static bool IsDiscardLabel(SwitchLabelSyntax label) =>
        label is DefaultSwitchLabelSyntax || label is CasePatternSwitchLabelSyntax { WhenClause: null } pattern && IsDiscard(pattern.Pattern);

    private static bool IsDiscard(PatternSyntax pattern) =>
        pattern is DiscardPatternSyntax
        || pattern is VarPatternSyntax { Designation: DiscardDesignationSyntax }
        || pattern is ParenthesizedPatternSyntax parenthesized && IsDiscard(parenthesized.Pattern)
        || pattern is RecursivePatternSyntax { PositionalPatternClause: { } positional, Type: null, PropertyPatternClause: null, Designation: null }
            && positional.Subpatterns.All(s => IsDiscard(s.Pattern));

    private sealed class ClosedHierarchies
    {
        private readonly Lazy<Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>> _derived;

        public ClosedHierarchies(Compilation compilation) =>
            _derived = new Lazy<Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>>(() => Build(compilation.Assembly.GlobalNamespace));

        public bool IsClosed(ITypeSymbol? type)
        {
            if (type is null)
            {
                return false;
            }

            if (type.SpecialType == SpecialType.System_Boolean)
            {
                return true;
            }

            if (type is INamedTypeSymbol { IsTupleType: true } tuple)
            {
                return tuple.TupleElements.Length > 0 && tuple.TupleElements.All(e => e.Type.SpecialType == SpecialType.System_Boolean || IsClosedHierarchy(e.Type));
            }

            return IsClosedHierarchy(type);
        }

        private bool IsClosedHierarchy(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface } named || named.IsSealed || !named.Locations.Any(l => l.IsInSource))
            {
                return false;
            }

            if (named.TypeKind == TypeKind.Class && !named.IsAbstract)
            {
                return false;
            }

            if (IsExternallyExtensible(named) || !_derived.Value.TryGetValue(named.OriginalDefinition, out var derived))
            {
                return false;
            }

            return derived.All(d => d.IsAbstract || d.IsSealed || d.TypeKind == TypeKind.Struct);
        }

        private static bool IsExternallyExtensible(INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                {
                    return false;
                }
            }

            return type.TypeKind == TypeKind.Interface
                || type.InstanceConstructors.Any(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal);
        }

        private static Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> Build(INamespaceSymbol root)
        {
            var result = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
            var pending = new Stack<INamespaceOrTypeSymbol>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                foreach (var member in pending.Pop().GetMembers().OfType<INamespaceOrTypeSymbol>())
                {
                    pending.Push(member);
                    if (member is INamedTypeSymbol type)
                    {
                        Register(result, type);
                    }
                }
            }

            return result;
        }

        private static void Register(Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> result, INamedTypeSymbol type)
        {
            var parents = type.AllInterfaces.Select(i => i.OriginalDefinition).ToList();
            for (var parent = type.BaseType; parent is { SpecialType: not SpecialType.System_Object }; parent = parent.BaseType)
            {
                parents.Add(parent.OriginalDefinition);
            }

            foreach (var parent in parents)
            {
                if (!result.TryGetValue(parent, out var list))
                {
                    list = [];
                    result[parent] = list;
                }

                list.Add(type);
            }
        }
    }
}
