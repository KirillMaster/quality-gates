namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ErrorFlowAnalyzer : QgAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.CatchInCore, Descriptors.CatchAllInAdapter, Descriptors.ManualRetry,
        Descriptors.CatchCancellation, Descriptors.ConsumerCatch);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var roles = new Roles(new CompilationConfig(start.Options, start.Compilation), start.Compilation);
            start.RegisterSyntaxNodeAction(c => AnalyzeCatch(c, roles), SyntaxKind.CatchClause);
            start.RegisterSyntaxNodeAction(c => AnalyzeDelay(c, roles), SyntaxKind.InvocationExpression);
        });

    private static void AnalyzeCatch(SyntaxNodeAnalysisContext context, Roles roles)
    {
        var clause = (CatchClauseSyntax)context.Node;
        var layer = roles.LayerOf(context.ContainingSymbol, clause.SyntaxTree);
        if (layer == Layer.Test)
        {
            return;
        }

        var type = Roles.TypeOf(context.ContainingSymbol);
        if (roles.IsConsumer(type, clause.SyntaxTree))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ConsumerCatch, clause.CatchKeyword.GetLocation(), type!.Name));
            return;
        }

        var caught = clause.Declaration is null ? null : context.SemanticModel.GetTypeInfo(clause.Declaration.Type, context.CancellationToken).Type;
        var boundary = roles.IsBoundary(type, clause);
        var rethrows = Rethrows(clause);
        if (roles.IsCancellation(caught) && !boundary && !rethrows)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.CatchCancellation, clause.CatchKeyword.GetLocation(), caught!.Name));
        }

        if (boundary)
        {
            return;
        }

        if (layer == Layer.Core)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.CatchInCore, clause.CatchKeyword.GetLocation()));
        }
        else if (layer == Layer.Adapter && clause.Filter is null && IsCatchAll(caught) && !rethrows)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.CatchAllInAdapter, clause.CatchKeyword.GetLocation(), caught?.Name ?? "all"));
        }
    }

    private static void AnalyzeDelay(SyntaxNodeAnalysisContext context, Roles roles)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => string.Empty,
        };
        if (name is not ("Delay" or "Sleep") || context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
        {
            return;
        }

        var full = method.ContainingType?.ToDisplayString() + "." + method.Name;
        if (full is not ("System.Threading.Tasks.Task.Delay" or "System.Threading.Thread.Sleep")
            || roles.LayerOf(context.ContainingSymbol, invocation.SyntaxTree) == Layer.Test
            || roles.Config.IsCompositionRoot(invocation)
            || roles.IsHostedService(Roles.TypeOf(context.ContainingSymbol)))
        {
            return;
        }

        var where = RetryContext(invocation);
        if (where is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ManualRetry, invocation.GetLocation(), method.ContainingType!.Name + "." + method.Name, where));
        }
    }

    private static string? RetryContext(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MemberDeclarationSyntax:
                    return null;
                case CatchClauseSyntax:
                    return "catch block";
                case WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax when HasTryCatch(ancestor):
                    return "retry loop";
            }
        }

        return null;
    }

    private static bool HasTryCatch(SyntaxNode loop) =>
        loop.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
            .OfType<TryStatementSyntax>().Any(t => t.Catches.Count > 0);

    private static bool Rethrows(CatchClauseSyntax clause) =>
        clause.Block.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
            .OfType<ThrowStatementSyntax>().Any(t => t.Expression is null);

    private static bool IsCatchAll(ITypeSymbol? type) =>
        type is null || type.SpecialType == SpecialType.System_Object
        || type.ToDisplayString() is "System.Exception" or "System.SystemException";
}
