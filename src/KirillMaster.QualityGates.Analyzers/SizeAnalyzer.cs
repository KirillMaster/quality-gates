namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SizeAnalyzer : QgAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.TypeTooLong, Descriptors.MethodTooLong, Descriptors.Complexity, Descriptors.Nesting,
        Descriptors.TooManyParameters, Descriptors.TooManyDependencies, Descriptors.CognitiveComplexity);

    protected override void Register(AnalysisContext context)
    {
        context.RegisterSyntaxNodeAction(AnalyzeType, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration, SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeMember, SyntaxKind.MethodDeclaration, SyntaxKind.ConstructorDeclaration, SyntaxKind.OperatorDeclaration, SyntaxKind.LocalFunctionStatement);
    }

    private static void AnalyzeType(SyntaxNodeAnalysisContext context)
    {
        var type = (TypeDeclarationSyntax)context.Node;
        var name = type.Identifier.ValueText;
        var tree = type.SyntaxTree;
        var maxLines = Thresholds.Get(context.Options, tree, "qg_max_type_lines", Descriptors.MaxTypeLines);
        var lines = Metrics.Lines(type);
        if (lines > maxLines)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.TypeTooLong, type.Identifier.GetLocation(), name, lines, maxLines));
        }

        var maxDeps = Thresholds.Get(context.Options, tree, "qg_max_dependencies", Descriptors.MaxDependencies);
        var primary = type.ParameterList?.Parameters.Count ?? 0;
        if (primary > maxDeps)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.TooManyDependencies, type.Identifier.GetLocation(), name, primary, maxDeps));
        }
    }

    private static void AnalyzeMember(SyntaxNodeAnalysisContext context)
    {
        var (name, location, body, parameters) = Describe(context.Node);
        if (body is null)
        {
            return;
        }

        var tree = context.Node.SyntaxTree;
        var maxLines = Thresholds.Get(context.Options, tree, "qg_max_method_lines", Descriptors.MaxMethodLines);
        var lines = Metrics.Lines(body);
        if (lines > maxLines)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.MethodTooLong, location, name, lines, maxLines));
        }

        var maxComplexity = Thresholds.Get(context.Options, tree, "qg_max_complexity", Descriptors.MaxComplexity);
        var complexity = Metrics.Complexity(body);
        if (complexity > maxComplexity)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Complexity, location, name, complexity, maxComplexity));
        }

        if (context.Node is not LocalFunctionStatementSyntax)
        {
            var maxCognitive = Thresholds.Get(context.Options, tree, "qg_max_cognitive_complexity", Descriptors.MaxCognitiveComplexity);
            var cognitive = CognitiveComplexity.Of(body, name);
            if (cognitive > maxCognitive)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.CognitiveComplexity, location, name, cognitive, maxCognitive));
            }
        }

        var maxNesting = Thresholds.Get(context.Options, tree, "qg_max_nesting", Descriptors.MaxNesting);
        var nesting = Metrics.Nesting(body);
        if (nesting > maxNesting)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.Nesting, location, name, nesting, maxNesting));
        }

        ReportParameters(context, name, location, parameters);
    }

    private static void ReportParameters(SyntaxNodeAnalysisContext context, string name, Location location, ParameterListSyntax? parameters)
    {
        var count = parameters?.Parameters.Count ?? 0;
        var tree = context.Node.SyntaxTree;
        if (context.Node is ConstructorDeclarationSyntax)
        {
            var maxDeps = Thresholds.Get(context.Options, tree, "qg_max_dependencies", Descriptors.MaxDependencies);
            if (count > maxDeps)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.TooManyDependencies, location, name, count, maxDeps));
            }

            return;
        }

        var maxParameters = Thresholds.Get(context.Options, tree, "qg_max_parameters", Descriptors.MaxParameters);
        if (count > maxParameters)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.TooManyParameters, location, name, count, maxParameters));
        }
    }

    private static (string Name, Location Location, SyntaxNode? Body, ParameterListSyntax? Parameters) Describe(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => (m.Identifier.ValueText, m.Identifier.GetLocation(), (SyntaxNode?)m.Body ?? m.ExpressionBody, m.ParameterList),
        ConstructorDeclarationSyntax c => (c.Identifier.ValueText, c.Identifier.GetLocation(), (SyntaxNode?)c.Body ?? c.ExpressionBody, c.ParameterList),
        OperatorDeclarationSyntax o => (o.OperatorToken.ValueText, o.OperatorToken.GetLocation(), (SyntaxNode?)o.Body ?? o.ExpressionBody, o.ParameterList),
        LocalFunctionStatementSyntax l => (l.Identifier.ValueText, l.Identifier.GetLocation(), (SyntaxNode?)l.Body ?? l.ExpressionBody, l.ParameterList),
        _ => (string.Empty, node.GetLocation(), null, null),
    };
}
