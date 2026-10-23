using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HygieneAnalyzer : QgAnalyzer
{
    private static readonly Regex Todo = new(@"\bTODO\b", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
    private static readonly Regex Banner = new(@"^(//|/\*)\s*[=\-*/#~_]{4,}", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.EmptyCatch, Descriptors.ThrowCaught, Descriptors.InterpolatedLog,
        Descriptors.TodoWithoutIssue, Descriptors.BannerComment);

    protected override void Register(AnalysisContext context)
    {
        context.RegisterSyntaxNodeAction(AnalyzeCatch, SyntaxKind.CatchClause);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxTreeAction(AnalyzeComments);
    }

    private static void AnalyzeCatch(SyntaxNodeAnalysisContext context)
    {
        var clause = (CatchClauseSyntax)context.Node;
        if (clause.Block.Statements.Count == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.EmptyCatch, clause.CatchKeyword.GetLocation()));
        }

        var caught = clause.Declaration?.Identifier.ValueText;
        if (string.IsNullOrEmpty(caught))
        {
            return;
        }

        foreach (var rethrow in clause.Block.DescendantNodes().OfType<ThrowStatementSyntax>())
        {
            if (rethrow.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == caught)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ThrowCaught, rethrow.GetLocation(), caught));
            }
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is MemberAccessExpressionSyntax member
            && LogNames.IsLogMethod(member.Name.Identifier.ValueText)
            && invocation.ArgumentList.Arguments.Any(a => a.Expression is InterpolatedStringExpressionSyntax))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.InterpolatedLog, invocation.GetLocation(), member.Name.Identifier.ValueText));
        }
    }

    private static void AnalyzeComments(SyntaxTreeAnalysisContext context)
    {
        var root = context.Tree.GetRoot(context.CancellationToken);
        foreach (var trivia in root.DescendantTrivia())
        {
            if (!IsComment(trivia))
            {
                continue;
            }

            var text = trivia.ToFullString();
            if (Todo.IsMatch(text) && !Symbols.IssueReference.IsMatch(text))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.TodoWithoutIssue, trivia.GetLocation()));
            }

            if (Banner.IsMatch(text))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.BannerComment, trivia.GetLocation()));
            }
        }
    }

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
        || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
        || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
        || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);
}
