using Microsoft.CodeAnalysis.Operations;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TestAnalyzer : QgAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Descriptors.SkipWithoutIssue, Descriptors.NondeterministicTest, Descriptors.TooManyAsserts);

    protected override void Register(AnalysisContext context)
    {
        context.RegisterCompilationStartAction(start =>
        {
            if (!Symbols.IsTestCompilation(start.Compilation))
            {
                return;
            }

            start.RegisterSymbolAction(AnalyzeTestMethod, SymbolKind.Method);
            start.RegisterOperationAction(AnalyzeProperty, OperationKind.PropertyReference);
            start.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
            start.RegisterOperationAction(AnalyzeCreation, OperationKind.ObjectCreation);
        });
    }

    private static void AnalyzeTestMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        foreach (var attribute in method.GetAttributes())
        {
            var skip = SkipReason(attribute);
            if (skip is not null && !Symbols.IssueReference.IsMatch(skip))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.SkipWithoutIssue, method.Locations[0]));
            }
        }

        if (!Symbols.IsTestMethod(method))
        {
            return;
        }

        var tree = method.Locations[0].SourceTree;
        var max = tree is null ? Descriptors.MaxAsserts : Thresholds.Get(context.Options, tree, "qg_max_asserts", Descriptors.MaxAsserts);
        var asserts = method.DeclaringSyntaxReferences
            .SelectMany(r => r.GetSyntax(context.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Count(IsAssert);
        if (asserts > max)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.TooManyAsserts, method.Locations[0], method.Name, asserts, max));
        }
    }

    private static string? SkipReason(AttributeData attribute)
    {
        if (Symbols.InheritsFrom(attribute.AttributeClass, "Xunit.FactAttribute"))
        {
            return attribute.NamedArguments.FirstOrDefault(a => a.Key == "Skip").Value.Value as string;
        }

        if (attribute.AttributeClass?.Name == "IgnoreAttribute")
        {
            return attribute.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty;
        }

        return null;
    }

    private static void AnalyzeProperty(OperationAnalysisContext context)
    {
        var property = ((IPropertyReferenceOperation)context.Operation).Property;
        var owner = property.ContainingType.ToDisplayString();
        if (owner is "System.DateTime" or "System.DateTimeOffset" && property.Name is "Now" or "UtcNow" or "Today")
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NondeterministicTest, context.Operation.Syntax.GetLocation(), owner + "." + property.Name));
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        if (method.Name == "Sleep" && method.ContainingType.ToDisplayString() == "System.Threading.Thread")
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NondeterministicTest, context.Operation.Syntax.GetLocation(), "Thread.Sleep"));
        }
    }

    private static void AnalyzeCreation(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Arguments.Length == 0 && creation.Type?.ToDisplayString() == "System.Random")
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NondeterministicTest, creation.Syntax.GetLocation(), "new Random()"));
        }
    }

    private static bool IsAssert(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Assert" } } => true,
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Should" } => true,
        _ => false,
    };
}
