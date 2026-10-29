using System.Collections.Concurrent;
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
        && parsed >= 0
            ? parsed
            : fallback;

    public static ImmutableHashSet<string> GetList(AnalyzerOptions options, SyntaxTree tree, string key, string fallback)
    {
        var raw = options.AnalyzerConfigOptionsProvider.GetOptions(tree).TryGetValue(key, out var value) ? value : fallback;
        return raw.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class CompilationConfig
{
    private readonly AnalyzerOptions _options;
    private readonly string _assembly;
    private readonly ConcurrentDictionary<(SyntaxTree Tree, string Key), ImmutableHashSet<string>> _lists = new();
    private readonly ConcurrentDictionary<SyntaxTree, bool> _rootAssemblyByTree = new();

    public CompilationConfig(AnalyzerOptions options, Compilation compilation)
    {
        _options = options;
        _assembly = compilation.AssemblyName ?? string.Empty;
    }

    public AnalyzerOptions Options => _options;

    public ImmutableHashSet<string> List(SyntaxTree tree, string key, string fallback) =>
        _lists.GetOrAdd((tree, key), k => Thresholds.GetList(_options, k.Tree, k.Key, fallback));

    public bool IsCompositionRoot(SyntaxNode node)
    {
        var tree = node.SyntaxTree;
        return string.Equals(System.IO.Path.GetFileName(tree.FilePath), "Program.cs", StringComparison.OrdinalIgnoreCase)
            || node.AncestorsAndSelf().OfType<GlobalStatementSyntax>().Any()
            || _rootAssemblyByTree.GetOrAdd(tree, t => List(t, "qg_composition_root_suffixes", Descriptors.CompositionRootSuffixes)
                .Any(suffix => _assembly.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }
}

internal static class Tokens
{
    public static string[] Split(string identifier)
    {
        var tokens = new System.Collections.Generic.List<string>();
        var start = -1;
        for (var i = 0; i <= identifier.Length; i++)
        {
            var c = i < identifier.Length ? identifier[i] : '_';
            var boundary = !char.IsLetter(c)
                || (start >= 0 && char.IsUpper(c) && (char.IsLower(identifier[i - 1])
                    || (i + 1 < identifier.Length && char.IsUpper(identifier[i - 1]) && char.IsLower(identifier[i + 1]))));
            if (boundary && start >= 0)
            {
                tokens.Add(identifier.Substring(start, i - start));
                start = -1;
            }

            if (start < 0 && char.IsLetter(c))
            {
                start = i;
            }
        }

        return tokens.ToArray();
    }
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
            IFieldSymbol => false,
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
