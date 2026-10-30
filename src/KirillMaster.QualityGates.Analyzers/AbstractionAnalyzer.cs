using System.Collections.Generic;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AbstractionAnalyzer : QgAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ReadabilityDescriptors.MixedAbstraction, ReadabilityDescriptors.HandlerDoesWork,
        ReadabilityDescriptors.HelperAboveCaller, ReadabilityDescriptors.LawOfDemeter);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            if (string.Equals(Settings.Layer(start.Options), "test", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var assembly = start.Compilation.Assembly;
            start.RegisterSyntaxNodeAction(c => AnalyzeMethod(c, assembly), SyntaxKind.MethodDeclaration);
            start.RegisterSyntaxNodeAction(AnalyzeType, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration, SyntaxKind.RecordDeclaration);
            start.RegisterSyntaxNodeAction(AnalyzeChain, SyntaxKind.SimpleMemberAccessExpression);
        });

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context, IAssemblySymbol assembly)
    {
        var declaration = (MethodDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } method || method.IsOverride)
        {
            return;
        }

        var statements = Flatten(declaration.Body?.Statements ?? default).ToList();
        if (IsInputHandler(method) && method.DeclaredAccessibility == Accessibility.Public)
        {
            CheckHandler(context, declaration, method, statements);
        }

        if (Settings.IsCore(context.Options, declaration.SyntaxTree, method.ContainingType))
        {
            CheckMixed(context, declaration, method, statements, assembly);
        }
    }

    private static IEnumerable<StatementSyntax> Flatten(IEnumerable<StatementSyntax> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case BlockSyntax block:
                    foreach (var inner in Flatten(block.Statements))
                    {
                        yield return inner;
                    }

                    break;
                case IfStatementSyntax conditional when !LowLevel.IsGuard(conditional):
                    foreach (var inner in Flatten(Branches(conditional)))
                    {
                        yield return inner;
                    }

                    break;
                case TryStatementSyntax tried:
                    foreach (var inner in Flatten(tried.Block.Statements.Concat(tried.Catches.SelectMany(c => c.Block.Statements))))
                    {
                        yield return inner;
                    }

                    break;
                case UsingStatementSyntax or LockStatementSyntax:
                    foreach (var inner in Flatten(statement.ChildNodes().OfType<StatementSyntax>()))
                    {
                        yield return inner;
                    }

                    break;
                default:
                    yield return statement;
                    break;
            }
        }
    }

    private static IEnumerable<StatementSyntax> Branches(IfStatementSyntax conditional)
    {
        yield return conditional.Statement;
        if (conditional.Else is not null)
        {
            yield return conditional.Else.Statement;
        }
    }

    private static bool IsInputHandler(IMethodSymbol method) =>
        method.ContainingType.AllInterfaces.Any(i => i.Name.Length > 8 && i.Name[0] == 'I' && i.Name.EndsWith("Handler", StringComparison.Ordinal)
            && ("." + i.ContainingNamespace.ToDisplayString() + ".").IndexOf("." + ReadabilityDescriptors.InputPortNamespace + ".", StringComparison.Ordinal) >= 0);

    private static void CheckHandler(SyntaxNodeAnalysisContext context, MethodDeclarationSyntax declaration, IMethodSymbol method, List<StatementSyntax> statements)
    {
        foreach (var statement in statements.Where(s => !LowLevel.IsGuard(s)))
        {
            var kind = LowLevel.Find(statement, context.SemanticModel);
            if (kind is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.HandlerDoesWork, declaration.Identifier.GetLocation(), method.Name, kind));
                return;
            }
        }
    }

    private static void CheckMixed(SyntaxNodeAnalysisContext context, MethodDeclarationSyntax declaration, IMethodSymbol method, List<StatementSyntax> statements, IAssemblySymbol assembly)
    {
        var tree = declaration.SyntaxTree;
        var effect = statements.Where(s => !LowLevel.IsGuard(s)).ToList();
        var low = effect.Count(s => LowLevel.Find(s, context.SemanticModel) is not null);
        var own = effect.Count(s => LowLevel.Find(s, context.SemanticModel) is null && IsOwnCall(s, context.SemanticModel, assembly));
        var composer = Thresholds.Get(context.Options, tree, "qg_composer_percent", ReadabilityDescriptors.ComposerPercent);
        var worker = Thresholds.Get(context.Options, tree, "qg_worker_percent", ReadabilityDescriptors.WorkerPercent);
        var minOwn = Thresholds.Get(context.Options, tree, "qg_min_own_statements", ReadabilityDescriptors.MinOwnStatements);
        var minLow = Thresholds.Get(context.Options, tree, "qg_min_low_statements", ReadabilityDescriptors.MinLowStatements);
        if (effect.Count == 0 || own < minOwn || low < minLow)
        {
            return;
        }

        var percent = own * 100 / effect.Count;
        if (percent < composer && percent > worker)
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.MixedAbstraction, declaration.Identifier.GetLocation(), method.Name, own, low, percent));
        }
    }

    private static bool IsOwnCall(SyntaxNode statement, SemanticModel model, IAssemblySymbol assembly) =>
        statement.DescendantNodesAndSelf(n => n is not LocalFunctionStatementSyntax).Any(n =>
            n is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
            && model.GetSymbolInfo(n).Symbol is IMethodSymbol called
            && SymbolEqualityComparer.Default.Equals(called.ContainingAssembly, assembly));

    private static void AnalyzeType(SyntaxNodeAnalysisContext context)
    {
        var type = (TypeDeclarationSyntax)context.Node;
        var owner = context.SemanticModel.GetDeclaredSymbol(type, context.CancellationToken);
        if (owner is null || owner.GetMembers().OfType<IMethodSymbol>().Any(Symbols.IsTestMethod))
        {
            return;
        }

        var methods = type.Members.OfType<BaseMethodDeclarationSyntax>().Where(m => m.Body is not null || m.ExpressionBody is not null).ToList();
        var firstCaller = new Dictionary<IMethodSymbol, (MethodDeclarationSyntax Caller, int Position)>(SymbolEqualityComparer.Default);
        foreach (var caller in methods.OfType<MethodDeclarationSyntax>())
        {
            var self = context.SemanticModel.GetDeclaredSymbol(caller, context.CancellationToken);
            foreach (var call in caller.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (context.SemanticModel.GetSymbolInfo(call, context.CancellationToken).Symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary } called
                    && !SymbolEqualityComparer.Default.Equals(called, self) && called.DeclaredAccessibility == Accessibility.Private
                    && SymbolEqualityComparer.Default.Equals(called.ContainingType, owner))
                {
                    var position = caller.SpanStart;
                    if (!firstCaller.TryGetValue(called, out var known) || position < known.Position)
                    {
                        firstCaller[called] = (caller, position);
                    }
                }
            }
        }

        foreach (var helper in methods.OfType<MethodDeclarationSyntax>())
        {
            if (context.SemanticModel.GetDeclaredSymbol(helper, context.CancellationToken) is { } symbol
                && firstCaller.TryGetValue(symbol, out var caller) && helper.SpanStart < caller.Position)
            {
                context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.HelperAboveCaller, helper.Identifier.GetLocation(), symbol.Name, caller.Caller.Identifier.ValueText));
            }
        }
    }

    private static void AnalyzeChain(SyntaxNodeAnalysisContext context)
    {
        var access = (MemberAccessExpressionSyntax)context.Node;
        SyntaxNode node = access.Parent is InvocationExpressionSyntax call && call.Expression == access ? call : access;
        if (node.Parent is MemberAccessExpressionSyntax parent && parent.Expression == node)
        {
            return;
        }

        var containing = context.ContainingSymbol?.ContainingType;
        if (containing is null || !Settings.IsCore(context.Options, access.SyntaxTree, containing))
        {
            return;
        }

        var steps = new List<ExpressionSyntax>();
        ExpressionSyntax current = access;
        while (true)
        {
            if (current is MemberAccessExpressionSyntax member)
            {
                steps.Add(member);
                current = member.Expression;
            }
            else if (current is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner })
            {
                steps.Add(inner);
                current = inner.Expression;
            }
            else
            {
                break;
            }
        }

        if (steps.Count <= 2 || context.SemanticModel.GetSymbolInfo(current, context.CancellationToken).Symbol is INamespaceOrTypeSymbol)
        {
            return;
        }

        steps.Reverse();
        var strangers = 0;
        for (var i = 0; i < steps.Count - 1; i++)
        {
            var step = (MemberAccessExpressionSyntax)steps[i];
            var symbol = context.SemanticModel.GetSymbolInfo(step, context.CancellationToken).Symbol;
            if (symbol is null || IsExempt(step, symbol, context.SemanticModel, context.CancellationToken))
            {
                return;
            }

            if (IsBehavioral(ResultType(symbol), context.Compilation.Assembly))
            {
                strangers++;
            }
        }

        if (strangers >= 2)
        {
            context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.LawOfDemeter, access.GetLocation(), access.ToString().Split('(')[0], strangers));
        }
    }

    private static bool IsExempt(MemberAccessExpressionSyntax step, ISymbol symbol, SemanticModel model, System.Threading.CancellationToken token)
    {
        var receiver = model.GetTypeInfo(step.Expression, token).Type;
        var result = ResultType(symbol);
        return symbol is IMethodSymbol { IsExtensionMethod: true }
            || symbol.ContainingType is { } owner && (IsFramework(owner) || owner.Name.EndsWith("Builder", StringComparison.Ordinal))
            || receiver is not null && result is not null && SymbolEqualityComparer.Default.Equals(receiver, result);
    }

    private static ITypeSymbol? ResultType(ISymbol symbol) => symbol switch
    {
        IPropertySymbol p => p.Type,
        IFieldSymbol f => f.Type,
        IMethodSymbol m => m.ReturnType,
        _ => null,
    };

    private static bool IsFramework(INamedTypeSymbol type)
    {
        var root = type.ContainingNamespace?.ToDisplayString().Split('.')[0];
        return root is "System" or "Microsoft";
    }

    private static bool IsBehavioral(ITypeSymbol? type, IAssemblySymbol assembly) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface, IsRecord: false } named
        && !IsFramework(named)
        && SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly)
        && named.GetMembers().OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared && m.Name is not ("Equals" or "GetHashCode" or "ToString"));
}
