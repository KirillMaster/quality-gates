namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DesignAnalyzer : QgAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.NullCollection, Descriptors.BoolFlagParameter, Descriptors.OutRefOutsideTry);

    protected override void Register(AnalysisContext context)
    {
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
        context.RegisterSyntaxNodeAction(AnalyzeReturn, SyntaxKind.ReturnStatement, SyntaxKind.ArrowExpressionClause);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.IsImplicitlyDeclared || method.IsOverride || method.IsExtern || Symbols.ImplementsInterface(method)
            || method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor))
        {
            return;
        }

        var isTry = method.Name.StartsWith("Try", StringComparison.Ordinal);
        var flagsAllowed = isTry || method.Name.StartsWith("Set", StringComparison.Ordinal)
            || method.DeclaredAccessibility == Accessibility.Private || Symbols.IsTestMethod(method);
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Locations.Length == 0)
            {
                continue;
            }

            if (!flagsAllowed && parameter.Type.SpecialType == SpecialType.System_Boolean)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.BoolFlagParameter, parameter.Locations[0], parameter.Name));
            }

            if (!isTry && method.Name != "Deconstruct" && parameter.RefKind is RefKind.Out or RefKind.Ref)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.OutRefOutsideTry, parameter.Locations[0], parameter.Name, parameter.RefKind == RefKind.Out ? "out" : "ref"));
            }
        }
    }

    private static void AnalyzeReturn(SyntaxNodeAnalysisContext context)
    {
        var value = context.Node switch
        {
            ReturnStatementSyntax r => r.Expression,
            ArrowExpressionClauseSyntax a => a.Expression,
            _ => null,
        };
        var isNull = value is DefaultExpressionSyntax
            || value is LiteralExpressionSyntax lit && (lit.IsKind(SyntaxKind.NullLiteralExpression) || lit.IsKind(SyntaxKind.DefaultLiteralExpression));
        var owner = context.Node.Ancestors().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or PropertyDeclarationSyntax);
        if (!isNull || owner is null or AnonymousFunctionExpressionSyntax)
        {
            return;
        }

        var returned = context.SemanticModel.GetDeclaredSymbol(owner, context.CancellationToken) switch
        {
            IMethodSymbol m => Symbols.UnwrapTask(m.ReturnType),
            IPropertySymbol p => p.Type,
            _ => null,
        };
        if (returned is not null && returned.NullableAnnotation != NullableAnnotation.Annotated && Symbols.IsCollection(returned))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NullCollection, value!.GetLocation(), returned.Name));
        }
    }
}
