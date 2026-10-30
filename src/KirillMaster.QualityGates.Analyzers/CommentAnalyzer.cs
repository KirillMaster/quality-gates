using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CommentAnalyzer : QgAnalyzer
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture;

    private static readonly Regex Byline = new(
        @"^(@author\b|(author|created|written|modified|edited)\s+by\b|author\s*:)",
        Options, TimeSpan.FromSeconds(1));

    private static readonly Regex DateByline = new(
        @"^[A-Z]{2,4}\s*[-:]?\s*\d{1,4}[./-]\d{1,2}[./-]\d{1,4}\b",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly ImmutableHashSet<string> TagMarkers = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "FIXME", "HACK", "XXX");

    private static readonly ImmutableHashSet<string> BenignFollowers = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase, "file", "files", "folder", "folders", "directory", "directories", "dir", "path", "storage");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ReadabilityDescriptors.NoRegion, ReadabilityDescriptors.AuthorByline, ReadabilityDescriptors.SuppressionWithoutJustification,
        ReadabilityDescriptors.WhyCommentMissing, ReadabilityDescriptors.TempMarker);

    protected override void Register(AnalysisContext context)
    {
        context.RegisterCompilationStartAction(start =>
        {
            var config = new CompilationConfig(start.Options, start.Compilation);
            start.RegisterSyntaxTreeAction(c => AnalyzeTrivia(c, config));
            start.RegisterSyntaxNodeAction(AnalyzeAttribute, SyntaxKind.Attribute);
            start.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        });
    }

    private static void AnalyzeTrivia(SyntaxTreeAnalysisContext context, CompilationConfig config)
    {
        var root = context.Tree.GetRoot(context.CancellationToken);
        var markers = config.List(context.Tree, "qg_temp_markers", ReadabilityDescriptors.TempMarkers);
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
        if (Byline.IsMatch(text) || DateByline.IsMatch(text))
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.AuthorByline, trivia.GetLocation()));
        }

        var marker = FindMarker(text, markers);
        if (marker is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.TempMarker, trivia.GetLocation(), marker));
        }
    }

    private static string? FindMarker(string text, ImmutableHashSet<string> markers)
    {
        foreach (var marker in markers)
        {
            var index = 0;
            while ((index = text.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var end = index + marker.Length;
                var bounded = (index == 0 || !char.IsLetterOrDigit(text[index - 1])) && (end >= text.Length || !char.IsLetterOrDigit(text[end]));
                if (bounded && IsTemporary(text, marker, end))
                {
                    return marker;
                }

                index = end;
            }
        }

        return null;
    }

    private static bool IsTemporary(string text, string marker, int end)
    {
        if (TagMarkers.Contains(marker))
        {
            return !Symbols.IssueReference.IsMatch(text);
        }

        var next = text.Substring(end).TrimStart().Split(' ', ',', '.', ';', ':')[0];
        return !BenignFollowers.Contains(next);
    }

    private static void AnalyzeAttribute(SyntaxNodeAnalysisContext context)
    {
        var attribute = (AttributeSyntax)context.Node;
        var name = attribute.Name is QualifiedNameSyntax qualified ? qualified.Right.Identifier.ValueText : (attribute.Name as SimpleNameSyntax)?.Identifier.ValueText;
        var isSuppress = name is "SuppressMessage" or "SuppressMessageAttribute";
        var isObsolete = name is "Obsolete" or "ObsoleteAttribute";
        if (!isSuppress && !isObsolete)
        {
            return;
        }

        var type = context.SemanticModel.GetTypeInfo(attribute, context.CancellationToken).Type?.ToDisplayString();
        if (isSuppress && type == "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute")
        {
            var justification = attribute.ArgumentList?.Arguments
                .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Justification");
            var text = justification is null ? null : context.SemanticModel.GetConstantValue(justification.Expression, context.CancellationToken).Value as string;
            if (string.IsNullOrWhiteSpace(text) || text!.StartsWith("<Pending>", StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.SuppressionWithoutJustification, attribute.GetLocation(), "SuppressMessage"));
            }
        }
        else if (isObsolete && type == "System.ObsoleteAttribute")
        {
            var message = attribute.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals is null)?.Expression is { } expression
                ? context.SemanticModel.GetConstantValue(expression, context.CancellationToken).Value as string
                : null;
            if (message is null || !Symbols.IssueReference.IsMatch(message))
            {
                context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.TempMarker, attribute.GetLocation(), "[Obsolete] without ticket"));
            }
        }
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        SyntaxNode? body = method.Body ?? (SyntaxNode?)method.ExpressionBody;
        if (body is null || HasWhyComment(method)
            || context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken) is not { } symbol)
        {
            return;
        }

        var threshold = Thresholds.Get(context.Options, method.SyntaxTree, "qg_why_comment_cognitive_threshold", ReadabilityDescriptors.WhyCommentCognitiveThreshold);
        var cognitive = CognitiveComplexity.Of(body, symbol, context.SemanticModel);
        if (cognitive <= threshold || Symbols.IsTestMethod(symbol))
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
