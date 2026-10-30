using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Operations;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TestQualityAnalyzer : QgAnalyzer
{
    private static readonly ConcurrentDictionary<string, Regex?> Patterns = new(StringComparer.Ordinal);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ReadabilityDescriptors.NoLogicInTests, ReadabilityDescriptors.MockOwnPortsOnly,
        ReadabilityDescriptors.ArrangeTooLong, ReadabilityDescriptors.TestNamePattern);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            if (!Symbols.IsTestCompilation(start.Compilation))
            {
                return;
            }

            var assembly = start.Compilation.Assembly;
            start.RegisterSymbolAction(AnalyzeTestMethod, SymbolKind.Method);
            start.RegisterOperationAction(c => AnalyzeInvocation(c, assembly), OperationKind.Invocation);
            start.RegisterOperationAction(c => AnalyzeCreation(c, assembly), OperationKind.ObjectCreation);
        });

    private static void AnalyzeTestMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!Symbols.IsTestMethod(method) || method.Locations.Length == 0 || method.Locations[0].SourceTree is not { } tree)
        {
            return;
        }

        CheckName(context, method, tree);
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(context.CancellationToken) is not MethodDeclarationSyntax declaration)
            {
                continue;
            }

            CheckLogic(context, method, declaration, tree);
            CheckArrange(context, method, declaration, tree);
        }
    }

    private static void CheckName(SymbolAnalysisContext context, IMethodSymbol method, SyntaxTree tree)
    {
        var pattern = Settings.Text(context.Options, tree, "qg_test_name_pattern", ReadabilityDescriptors.TestPattern);
        var regex = Patterns.GetOrAdd(pattern, Compile);
        regex ??= Patterns.GetOrAdd(ReadabilityDescriptors.TestPattern, Compile);
        if (regex is not null && !regex.IsMatch(method.Name))
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.TestNamePattern, method.Locations[0], method.Name, pattern));
        }
    }

    private static Regex? Compile(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void CheckLogic(SymbolAnalysisContext context, IMethodSymbol method, MethodDeclarationSyntax declaration, SyntaxTree tree)
    {
        var exempt = Thresholds.GetList(context.Options, tree, "qg_test_logic_exempt_assemblies", ReadabilityDescriptors.TestLogicExemptAssemblies);
        var assembly = method.ContainingAssembly.Name;
        if (exempt.Any(e => assembly.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return;
        }

        var logic = declaration.DescendantNodes().FirstOrDefault(n => n is IfStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax
            or WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax);
        if (logic is not null)
        {
            var keyword = logic.ChildTokens().First().ValueText;
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.NoLogicInTests, logic.GetLocation(), method.Name, keyword));
        }
    }

    private static void CheckArrange(SymbolAnalysisContext context, IMethodSymbol method, MethodDeclarationSyntax declaration, SyntaxTree tree)
    {
        if (declaration.Body is null)
        {
            return;
        }

        var statements = declaration.Body.Statements;
        var act = statements.Select((s, i) => (s, i)).FirstOrDefault(p => p.s.GetLeadingTrivia().Any(t => IsActMarker(t))).i;
        var hasMarker = statements.Any(s => s.GetLeadingTrivia().Any(t => IsActMarker(t)));
        if (!hasMarker)
        {
            var firstAssert = statements.Select((s, i) => (s, i)).FirstOrDefault(p => ContainsAssert(p.s));
            if (firstAssert.s is null)
            {
                return;
            }

            act = Math.Max(firstAssert.i - 1, 0);
        }

        var max = Thresholds.Get(context.Options, tree, "qg_max_arrange_statements", ReadabilityDescriptors.MaxArrangeStatements);
        if (act > max)
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.ArrangeTooLong, method.Locations[0], method.Name, act, max));
        }
    }

    private static bool IsActMarker(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
        && string.Equals(CommentTrivia.Text(trivia).Split(' ', ':')[0], "Act", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAssert(SyntaxNode statement) =>
        statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i => i.Expression switch
        {
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Assert" } } => true,
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Should" or "Received" or "Verify" } => true,
            _ => false,
        });

    private static void AnalyzeInvocation(OperationAnalysisContext context, IAssemblySymbol assembly)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        var owner = method.ContainingType.ToDisplayString();
        var isFactory = owner == "NSubstitute.Substitute" && method.Name == "For"
            || owner == "FakeItEasy.A" && method.Name == "Fake"
            || owner == "Moq.Mock" && method.Name == "Of";
        if (isFactory && method.TypeArguments.Length > 0)
        {
            Check(context, method.TypeArguments[0], assembly, context.Operation.Syntax);
        }
    }

    private static void AnalyzeCreation(OperationAnalysisContext context, IAssemblySymbol assembly)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Type is INamedTypeSymbol { IsGenericType: true } type && type.ConstructedFrom.ToDisplayString() == "Moq.Mock<T>")
        {
            Check(context, type.TypeArguments[0], assembly, creation.Syntax);
        }
    }

    private static void Check(OperationAnalysisContext context, ITypeSymbol mocked, IAssemblySymbol assembly, SyntaxNode syntax)
    {
        var tree = syntax.SyntaxTree;
        var display = mocked.OriginalDefinition.ContainingNamespace + "." + mocked.Name;
        var allowed = Thresholds.GetList(context.Options, tree, "qg_allowed_mock_types", ReadabilityDescriptors.AllowedMockTypes);
        if (allowed.Any(a => display.StartsWith(a, StringComparison.Ordinal)))
        {
            return;
        }

        var segments = Thresholds.GetList(context.Options, tree, "qg_port_namespace_segments", ReadabilityDescriptors.PortNamespaceSegments);
        var prefixes = Thresholds.GetList(context.Options, tree, "qg_own_assembly_prefixes", assembly.Name.Split('.')[0]);
        var isOwn = mocked.ContainingAssembly is { } owner && (SymbolEqualityComparer.Default.Equals(owner, assembly)
            || prefixes.Any(p => owner.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        var isPort = mocked.TypeKind == TypeKind.Interface && isOwn
            && mocked.ContainingNamespace.ToDisplayString().Split('.').Any(segments.Contains);
        if (!isPort)
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.MockOwnPortsOnly, syntax.GetLocation(), mocked.Name));
        }
    }
}
