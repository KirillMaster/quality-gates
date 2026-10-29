namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StructureAnalyzer : QgAnalyzer
{
    private static readonly ImmutableHashSet<string> SourceGeneratorAttributes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "GeneratedRegex", "LoggerMessage", "JsonSerializable", "JsonSourceGenerationOptions", "LibraryImport",
        "GeneratedCode", "CompilerGenerated");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.PassThroughMethod, Descriptors.ShallowClass, Descriptors.NoPartialClass);

    protected override void Register(AnalysisContext context)
    {
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeClass, SyntaxKind.ClassDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (IsExempt(context, method) || !PassThrough.IsCandidate(method, context.SemanticModel, context.Compilation, context.CancellationToken))
        {
            return;
        }

        var target = PassThrough.Target(method, context.SemanticModel, context.Compilation, context.CancellationToken);
        if (target is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.PassThroughMethod, method.Identifier.GetLocation(), method.Identifier.ValueText, target));
        }
    }

    private static void AnalyzeClass(SyntaxNodeAnalysisContext context)
    {
        var type = (ClassDeclarationSyntax)context.Node;
        if (type.Modifiers.Any(SyntaxKind.PartialKeyword) && !HasGeneratorAttribute(type))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NoPartialClass, type.Identifier.GetLocation(), type.Identifier.ValueText));
        }

        if (IsExempt(context, type))
        {
            return;
        }

        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => PassThrough.IsCandidate(m, context.SemanticModel, context.Compilation, context.CancellationToken))
            .ToArray();
        var minMethods = Thresholds.Get(context.Options, type.SyntaxTree, "qg_min_shallow_methods", Descriptors.MinShallowMethods);
        if (methods.Length < minMethods)
        {
            return;
        }

        var maxPercent = Thresholds.Get(context.Options, type.SyntaxTree, "qg_max_pass_through_percent", Descriptors.MaxPassThroughPercent);
        var forwarding = methods.Count(m => PassThrough.Target(m, context.SemanticModel, context.Compilation, context.CancellationToken) is not null);
        if (forwarding * 100 >= methods.Length * maxPercent)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ShallowClass, type.Identifier.GetLocation(), type.Identifier.ValueText, forwarding, methods.Length));
        }
    }

    private static bool IsExempt(SyntaxNodeAnalysisContext context, SyntaxNode node)
    {
        if (CompositionRoot.IsExempt(context.Options, context.Compilation, node))
        {
            return true;
        }

        var folders = Thresholds.GetList(context.Options, node.SyntaxTree, "qg_pass_through_exempt_folders", Descriptors.PassThroughExemptFolders);
        return node.SyntaxTree.FilePath.Split('/', '\\').Any(folders.Contains);
    }

    private static bool HasGeneratorAttribute(ClassDeclarationSyntax type) =>
        type.AttributeLists.Concat(type.Members.SelectMany(m => m.AttributeLists))
            .SelectMany(list => list.Attributes)
            .Any(a => SourceGeneratorAttributes.Contains(AttributeName(a)));

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name is QualifiedNameSyntax qualified ? qualified.Right.Identifier.ValueText : attribute.Name.ToString();
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name.Substring(0, name.Length - "Attribute".Length) : name;
    }
}

internal static class PassThrough
{
    public static bool IsCandidate(MethodDeclarationSyntax method, SemanticModel model, Compilation compilation, System.Threading.CancellationToken token)
    {
        var symbol = model.GetDeclaredSymbol(method, token);
        return symbol is { DeclaredAccessibility: Accessibility.Public, IsStatic: false, IsOverride: false, IsAbstract: false }
            && symbol.ContainingType.TypeKind == TypeKind.Class
            && !symbol.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute")
            && !ImplementsExternalInterface(symbol, compilation);
    }

    public static string? Target(MethodDeclarationSyntax method, SemanticModel model, Compilation compilation, System.Threading.CancellationToken token)
    {
        if (Unwrap(SingleExpression(method)) is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call)
        {
            return null;
        }

        var receiver = access.Expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } self ? self.Name : access.Expression;
        var owner = model.GetDeclaredSymbol(method, token);
        var targetType = model.GetSymbolInfo(receiver, token).Symbol switch
        {
            IFieldSymbol field => field.Type,
            IParameterSymbol parameter when !SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, owner) => parameter.Type,
            _ => null,
        };
        if (targetType is not INamedTypeSymbol named
            || !SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, compilation.Assembly)
            || owner!.ContainingType.AllInterfaces.Contains(named, SymbolEqualityComparer.Default))
        {
            return null;
        }

        var parameters = method.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToImmutableHashSet();
        var args = call.ArgumentList.Arguments.Select(a => a.Expression).ToArray();
        var simple = args.All(a => a is IdentifierNameSyntax or LiteralExpressionSyntax or MemberAccessExpressionSyntax or DefaultExpressionSyntax);
        var bare = args.Count(a => a is IdentifierNameSyntax id && parameters.Contains(id.Identifier.ValueText));
        return simple && bare * 2 >= args.Length ? named.Name + "." + access.Name.Identifier.ValueText : null;
    }

    private static ExpressionSyntax? SingleExpression(MethodDeclarationSyntax method)
    {
        if (method.ExpressionBody is not null)
        {
            return method.ExpressionBody.Expression;
        }

        return method.Body is { Statements.Count: 1 } body
            ? body.Statements[0] switch
            {
                ReturnStatementSyntax r => r.Expression,
                ExpressionStatementSyntax e => e.Expression,
                _ => null,
            }
            : null;
    }

    private static ExpressionSyntax? Unwrap(ExpressionSyntax? expression)
    {
        while (true)
        {
            switch (expression)
            {
                case AwaitExpressionSyntax await:
                    expression = await.Expression;
                    break;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" } configure }:
                    expression = configure.Expression;
                    break;
                default:
                    return expression;
            }
        }
    }

    private static bool ImplementsExternalInterface(IMethodSymbol method, Compilation compilation) =>
        method.ContainingType.AllInterfaces
            .Where(i => !SymbolEqualityComparer.Default.Equals(i.ContainingAssembly, compilation.Assembly))
            .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
            .Any(i => SymbolEqualityComparer.Default.Equals(method.ContainingType.FindImplementationForInterfaceMember(i), method));
}
