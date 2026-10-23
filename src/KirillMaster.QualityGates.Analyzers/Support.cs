using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Analyzers;

public abstract class QgAnalyzer : DiagnosticAnalyzer
{
    public sealed override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        Register(context);
    }

    protected abstract void Register(AnalysisContext context);
}

internal static class Thresholds
{
    public static int Get(AnalyzerOptions options, SyntaxTree tree, string key, int fallback) =>
        options.AnalyzerConfigOptionsProvider.GetOptions(tree).TryGetValue(key, out var value)
        && int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}

internal static class Metrics
{
    public static int Lines(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
    }

    public static int Complexity(SyntaxNode body) => 1 + body.DescendantNodes().Count(IsDecisionPoint);

    public static int Nesting(SyntaxNode body) =>
        body.DescendantNodes().Where(IsNestingStatement).Select(Depth).DefaultIfEmpty(0).Max();

    private static bool IsDecisionPoint(SyntaxNode node) => node.Kind() switch
    {
        SyntaxKind.IfStatement or SyntaxKind.CaseSwitchLabel or SyntaxKind.CasePatternSwitchLabel
            or SyntaxKind.SwitchExpressionArm or SyntaxKind.ForStatement or SyntaxKind.ForEachStatement
            or SyntaxKind.WhileStatement or SyntaxKind.DoStatement or SyntaxKind.CatchClause
            or SyntaxKind.ConditionalExpression or SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression
            or SyntaxKind.CoalesceExpression or SyntaxKind.CoalesceAssignmentExpression => true,
        _ => false,
    };

    private static bool IsNestingStatement(SyntaxNode node) => node switch
    {
        IfStatementSyntax { Parent: ElseClauseSyntax } => false,
        IfStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax
            or DoStatementSyntax or SwitchStatementSyntax or TryStatementSyntax or UsingStatementSyntax
            or LockStatementSyntax => true,
        _ => false,
    };

    private static int Depth(SyntaxNode node) =>
        node.AncestorsAndSelf()
            .TakeWhile(n => n is not (MemberDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            .Count(IsNestingStatement);
}

internal static class LogNames
{
    private static readonly ImmutableHashSet<string> Methods = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Log", "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical",
        "Verbose", "Debug", "Information", "Warning", "Error", "Fatal");

    public static bool IsLogMethod(string name) => Methods.Contains(name);
}

internal static class Symbols
{
    public static readonly Regex IssueReference = new(@"#\d+|https?://", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    private static readonly string[] TestAttributes =
    [
        "NUnit.Framework.TestAttribute", "NUnit.Framework.TestCaseAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute",
    ];

    public static bool ImplementsInterface(ISymbol member) =>
        member switch
        {
            IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 } => true,
            IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 } => true,
            _ => member.ContainingType?.AllInterfaces
                .SelectMany(i => i.GetMembers())
                .Any(i => SymbolEqualityComparer.Default.Equals(member.ContainingType.FindImplementationForInterfaceMember(i), member)) ?? false,
        };

    public static ITypeSymbol UnwrapTask(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true, Name: "Task" or "ValueTask", TypeArguments.Length: 1 } t
            && t.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks"
            ? t.TypeArguments[0]
            : type;

    public static bool IsCollection(ITypeSymbol type) =>
        type.SpecialType != SpecialType.System_String
        && (type is IArrayTypeSymbol
            || type.SpecialType == SpecialType.System_Collections_IEnumerable
            || type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable));

    public static bool IsTestCompilation(Compilation compilation) =>
        compilation.GetTypeByMetadataName("Xunit.FactAttribute") is not null
        || TestAttributes.Any(a => compilation.GetTypeByMetadataName(a) is not null);

    public static bool IsTestMethod(IMethodSymbol method) =>
        method.GetAttributes().Any(a => InheritsFrom(a.AttributeClass, "Xunit.FactAttribute")
            || TestAttributes.Contains(a.AttributeClass?.ToDisplayString() ?? string.Empty, StringComparer.Ordinal));

    public static bool InheritsFrom(ITypeSymbol? type, string fullName)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (string.Equals(type.ToDisplayString(), fullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
