using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CommentAnalyzer : QgAnalyzer
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture;

    private static readonly Regex Byline = new(
        @"^(@author\b|(author|created|written|modified|edited|changed|added|fixed)\s+by\b|author\s*:|[A-Z]{2,4}\s*[-:]?\s*\d{1,4}[./-]\d{1,2}[./-]\d{1,4}\b)",
        Options, TimeSpan.FromSeconds(1));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ReadabilityDescriptors.NoRegion, ReadabilityDescriptors.AuthorByline, ReadabilityDescriptors.SuppressionWithoutJustification,
        ReadabilityDescriptors.WhyCommentMissing, ReadabilityDescriptors.TempMarker);

    protected override void Register(AnalysisContext context)
    {
        context.RegisterSyntaxTreeAction(AnalyzeTrivia);
        context.RegisterSyntaxNodeAction(AnalyzeAttribute, SyntaxKind.Attribute);
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeTrivia(SyntaxTreeAnalysisContext context)
    {
        var root = context.Tree.GetRoot(context.CancellationToken);
        var markers = Thresholds.GetList(context.Options, context.Tree, "qg_temp_markers", ReadabilityDescriptors.TempMarkers);
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.RegionDirectiveTrivia))
            {
                context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.NoRegion, trivia.GetLocation()));
            }
            else if (trivia.GetStructure() is PragmaWarningDirectiveTriviaSyntax pragma && pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword))
            {
                AnalyzePragma(context, pragma);
            }
            else if (CommentTrivia.IsPlainComment(trivia))
            {
                AnalyzeComment(context, trivia, markers);
            }
        }
    }

    private static void AnalyzePragma(SyntaxTreeAnalysisContext context, PragmaWarningDirectiveTriviaSyntax pragma)
    {
        var reason = pragma.DescendantTrivia().Where(CommentTrivia.IsPlainComment).Select(CommentTrivia.Text).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(reason))
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.SuppressionWithoutJustification, pragma.GetLocation(), "#pragma warning disable"));
        }

        context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.TempMarker, pragma.GetLocation(), "#pragma warning disable"));
    }

    private static void AnalyzeComment(SyntaxTreeAnalysisContext context, SyntaxTrivia trivia, ImmutableHashSet<string> markers)
    {
        var text = CommentTrivia.Text(trivia);
        if (Byline.IsMatch(text))
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.AuthorByline, trivia.GetLocation()));
        }

        var marker = markers.FirstOrDefault(m => text.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
        if (marker is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.TempMarker, trivia.GetLocation(), marker));
        }
    }

    private static void AnalyzeAttribute(SyntaxNodeAnalysisContext context)
    {
        var attribute = (AttributeSyntax)context.Node;
        if (context.SemanticModel.GetTypeInfo(attribute, context.CancellationToken).Type?.ToDisplayString() != "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute")
        {
            return;
        }

        var justification = attribute.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Justification");
        var text = justification is null ? null : context.SemanticModel.GetConstantValue(justification.Expression, context.CancellationToken).Value as string;
        if (string.IsNullOrWhiteSpace(text) || text!.StartsWith("<Pending>", StringComparison.Ordinal))
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.SuppressionWithoutJustification, attribute.GetLocation(), "SuppressMessage"));
        }
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        SyntaxNode? body = method.Body ?? (SyntaxNode?)method.ExpressionBody;
        if (body is null || context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken) is not { } symbol || Symbols.IsTestMethod(symbol))
        {
            return;
        }

        var threshold = Thresholds.Get(context.Options, method.SyntaxTree, "qg_why_comment_cognitive_threshold", ReadabilityDescriptors.WhyCommentCognitiveThreshold);
        var cognitive = CognitiveComplexity.Of(body, symbol, context.SemanticModel);
        if (cognitive <= threshold || HasWhyComment(method))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.WhyCommentMissing, method.Identifier.GetLocation(), method.Identifier.ValueText, cognitive, threshold));
    }

    private static bool HasWhyComment(MethodDeclarationSyntax method)
    {
        var inside = method.Body?.Statements.FirstOrDefault()?.GetLeadingTrivia() ?? default;
        return method.GetLeadingTrivia().Concat(method.Body?.OpenBraceToken.TrailingTrivia ?? default).Concat(inside).Any(CommentTrivia.IsPlainComment);
    }
}
