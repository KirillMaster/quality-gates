namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ErrorsAnalyzer : QgAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.SwallowedException, Descriptors.NullFromCatch, Descriptors.EnumSwitchWithoutThrow,
        Descriptors.EnumToEnumCast, Descriptors.AsyncVoid);

    protected override void Register(AnalysisContext context)
    {
        context.RegisterSyntaxNodeAction(AnalyzeCatch, SyntaxKind.CatchClause);
        context.RegisterSyntaxNodeAction(AnalyzeSwitchStatement, SyntaxKind.SwitchStatement);
        context.RegisterSyntaxNodeAction(AnalyzeSwitchExpression, SyntaxKind.SwitchExpression);
        context.RegisterSyntaxNodeAction(AnalyzeCast, SyntaxKind.CastExpression);
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
    }

    private static void AnalyzeCatch(SyntaxNodeAnalysisContext context)
    {
        var clause = (CatchClauseSyntax)context.Node;
        var body = clause.Block.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax).ToArray();
        foreach (var value in body.OfType<ReturnStatementSyntax>().Select(r => r.Expression).Where(IsNullLike))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NullFromCatch, value!.GetLocation(), value.ToString()));
        }

        var catchesAll = clause.Filter is null && (clause.Declaration is null || IsSystemException(context, clause.Declaration.Type));
        var handled = body.Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax) || body.OfType<InvocationExpressionSyntax>().Any(IsLogCall);
        if (catchesAll && clause.Block.Statements.Count > 0 && !handled)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.SwallowedException, clause.CatchKeyword.GetLocation()));
        }
    }

    private static void AnalyzeSwitchStatement(SyntaxNodeAnalysisContext context)
    {
        var node = (SwitchStatementSyntax)context.Node;
        var enumType = ExhaustiveEnum(context, node.Expression);
        if (enumType is null)
        {
            return;
        }

        var fallback = node.Sections.FirstOrDefault(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax || l is CasePatternSwitchLabelSyntax { Pattern: DiscardPatternSyntax }));
        if (fallback is null || !fallback.Statements.Any(s => s is ThrowStatementSyntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.EnumSwitchWithoutThrow, node.SwitchKeyword.GetLocation(), enumType.Name));
        }
    }

    private static void AnalyzeSwitchExpression(SyntaxNodeAnalysisContext context)
    {
        var node = (SwitchExpressionSyntax)context.Node;
        var enumType = ExhaustiveEnum(context, node.GoverningExpression);
        if (enumType is null)
        {
            return;
        }

        var last = node.Arms.LastOrDefault();
        var throwsOnRest = last is { Pattern: DiscardPatternSyntax or VarPatternSyntax { Designation: DiscardDesignationSyntax }, WhenClause: null, Expression: ThrowExpressionSyntax };
        if (!throwsOnRest)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.EnumSwitchWithoutThrow, node.SwitchKeyword.GetLocation(), enumType.Name));
        }
    }

    private static void AnalyzeCast(SyntaxNodeAnalysisContext context)
    {
        var cast = (CastExpressionSyntax)context.Node;
        var target = Unwrap(context.SemanticModel.GetTypeInfo(cast.Type, context.CancellationToken).Type);
        var source = Unwrap(context.SemanticModel.GetTypeInfo(cast.Expression, context.CancellationToken).Type);
        if (target?.TypeKind == TypeKind.Enum && source?.TypeKind == TypeKind.Enum && !SymbolEqualityComparer.Default.Equals(target, source))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.EnumToEnumCast, cast.GetLocation(), source.Name, target.Name));
        }
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.IsAsync && method.ReturnsVoid && !IsEventHandler(method))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.AsyncVoid, method.Locations[0], method.Name));
        }
    }

    private static ITypeSymbol? ExhaustiveEnum(SyntaxNodeAnalysisContext context, ExpressionSyntax governing)
    {
        var type = Unwrap(context.SemanticModel.GetTypeInfo(governing, context.CancellationToken).Type);
        var flags = type?.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute") ?? false;
        return type?.TypeKind == TypeKind.Enum && !flags ? type : null;
    }

    private static ITypeSymbol? Unwrap(ITypeSymbol? type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;

    private static bool IsNullLike(ExpressionSyntax? value) =>
        value is LiteralExpressionSyntax lit && (lit.IsKind(SyntaxKind.NullLiteralExpression) || lit.IsKind(SyntaxKind.DefaultLiteralExpression))
        || value is DefaultExpressionSyntax;

    private static bool IsSystemException(SyntaxNodeAnalysisContext context, TypeSyntax type) =>
        context.SemanticModel.GetTypeInfo(type, context.CancellationToken).Type?.ToDisplayString() == "System.Exception";

    private static bool IsLogCall(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax member && LogNames.IsLogMethod(member.Name.Identifier.ValueText);

    private static bool IsEventHandler(IMethodSymbol method) =>
        method.Parameters.Length == 2
        && method.Parameters[0].Type.SpecialType == SpecialType.System_Object
        && Symbols.InheritsFrom(method.Parameters[1].Type, "System.EventArgs");
}
