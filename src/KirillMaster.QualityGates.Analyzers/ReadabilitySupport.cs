using System.Collections.Generic;

namespace KirillMaster.QualityGates.Analyzers;

internal static class Settings
{
    public static string Text(AnalyzerOptions options, SyntaxTree tree, string key, string fallback) =>
        options.AnalyzerConfigOptionsProvider.GetOptions(tree).TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    public static string Layer(AnalyzerOptions options) =>
        options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.QualityGatesLayer", out var value) ? value : string.Empty;

    public static bool IsCore(AnalyzerOptions options, SyntaxTree tree, INamedTypeSymbol type)
    {
        var layer = Layer(options);
        if (layer.Length > 0)
        {
            return string.Equals(layer, "core", StringComparison.OrdinalIgnoreCase);
        }

        var segments = Thresholds.GetList(options, tree, "qg_core_namespace_segments", ReadabilityDescriptors.CoreNamespaceSegments);
        var parts = type.ContainingNamespace.ToDisplayString().Split('.');
        return parts.Any(segments.Contains) && !parts.Any(p => p is "Ports" or "Adapters" or "Runner" or "Tests" or "UnitTests" or "Specs");
    }
}

internal static class CommentTrivia
{
    public static IEnumerable<SyntaxTrivia> All(SyntaxNode root) =>
        root.DescendantTrivia(descendIntoTrivia: true).Where(IsPlainComment);

    public static bool IsPlainComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia);

    public static string Text(SyntaxTrivia trivia) =>
        trivia.ToString().TrimStart('/', '*').TrimEnd('*', '/').Trim();
}

internal static class Identifiers
{
    public static (string Name, Location Location)? Local(SyntaxNode node) => node switch
    {
        VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: not (FieldDeclarationSyntax or EventFieldDeclarationSyntax) } } v
            => (v.Identifier.ValueText, v.Identifier.GetLocation()),
        ForEachStatementSyntax f => (f.Identifier.ValueText, f.Identifier.GetLocation()),
        SingleVariableDesignationSyntax d => (d.Identifier.ValueText, d.Identifier.GetLocation()),
        ParameterSyntax { Parent: ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax } p => (p.Identifier.ValueText, p.Identifier.GetLocation()),
        ParameterSyntax { Parent: ParameterListSyntax { Parent: ParenthesizedLambdaExpressionSyntax or LocalFunctionStatementSyntax } } p => (p.Identifier.ValueText, p.Identifier.GetLocation()),
        _ => null,
    };

    public static bool IsDeclaredHere(ISymbol symbol) =>
        !symbol.IsImplicitlyDeclared && symbol.Locations.Length > 0 && symbol.Locations[0].IsInSource;
}

internal static class LowLevel
{
    private static readonly ImmutableHashSet<string> StringMethods = ImmutableHashSet.Create(
        StringComparer.Ordinal, "Substring", "Split", "Replace", "Trim", "TrimStart", "TrimEnd", "ToUpper", "ToLower", "ToUpperInvariant",
        "ToLowerInvariant", "Join", "Concat", "Format", "PadLeft", "PadRight", "Remove", "Insert", "IndexOf", "StartsWith", "EndsWith");

    public static string? Find(SyntaxNode statement, SemanticModel model)
    {
        foreach (var node in statement.DescendantNodesAndSelf(n => n is not LocalFunctionStatementSyntax))
        {
            var kind = Classify(node, model);
            if (kind is not null)
            {
                return kind;
            }
        }

        return null;
    }

    public static bool IsGuard(StatementSyntax statement) =>
        statement is IfStatementSyntax { Else: null } guard
        && (IsExit(guard.Statement) || guard.Statement is BlockSyntax { Statements.Count: 1 } block && IsExit(block.Statements[0]));

    private static bool IsExit(StatementSyntax statement) =>
        statement is ReturnStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax;

    private static string? Classify(SyntaxNode node, SemanticModel model)
    {
        switch (node)
        {
            case ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                return "loop";
            case SwitchStatementSyntax:
                return "switch";
            case ElementAccessExpressionSyntax:
                return "indexer";
            case InterpolatedStringExpressionSyntax:
                return "string operation";
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) || b.IsKind(SyntaxKind.SubtractExpression)
                || b.IsKind(SyntaxKind.MultiplyExpression) || b.IsKind(SyntaxKind.DivideExpression) || b.IsKind(SyntaxKind.ModuloExpression):
                return model.GetTypeInfo(b).Type?.SpecialType == SpecialType.System_String ? "string operation" : "arithmetic";
            case InvocationExpressionSyntax invocation when model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method:
                return InvocationKind(invocation, method);
            default:
                return null;
        }
    }

    private static string? InvocationKind(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        var owner = method.ContainingType.ToDisplayString();
        if (owner == "System.Linq.Enumerable" && invocation.ArgumentList.Arguments.Any(a => a.Expression is AnonymousFunctionExpressionSyntax))
        {
            return "LINQ";
        }

        if (owner == "System.String" && StringMethods.Contains(method.Name))
        {
            return "string operation";
        }

        return owner.StartsWith("System.Text.RegularExpressions.Regex", StringComparison.Ordinal) ? "regex" : null;
    }
}
