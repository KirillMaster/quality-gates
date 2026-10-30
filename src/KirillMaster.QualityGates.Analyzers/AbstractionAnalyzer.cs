using System.Collections.Concurrent;
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
            if (Symbols.IsTestCompilation(start.Compilation) || string.Equals(Settings.Layer(start.Options), "test", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var state = new State(start.Compilation.Assembly);
            start.RegisterSyntaxNodeAction(state.AnalyzeMethod, SyntaxKind.MethodDeclaration);
            start.RegisterSyntaxNodeAction(AnalyzeType, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration, SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
            start.RegisterSyntaxNodeAction(state.AnalyzeChain, SyntaxKind.SimpleMemberAccessExpression);
        });

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

    private static void AnalyzeType(SyntaxNodeAnalysisContext context)
    {
        var type = (TypeDeclarationSyntax)context.Node;
        var owner = context.SemanticModel.GetDeclaredSymbol(type, context.CancellationToken);
        if (owner is null || owner.GetMembers().OfType<IMethodSymbol>().Any(Symbols.IsTestMethod))
        {
            return;
        }

        var methods = type.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Body is not null || m.ExpressionBody is not null).ToList();
        var privateNames = new HashSet<string>(
            methods.Where(m => m.Modifiers.Any(SyntaxKind.PrivateKeyword) || !m.Modifiers.Any(k => k.IsKind(SyntaxKind.PublicKeyword) || k.IsKind(SyntaxKind.InternalKeyword) || k.IsKind(SyntaxKind.ProtectedKeyword)))
                .Select(m => m.Identifier.ValueText),
            StringComparer.Ordinal);
        if (privateNames.Count == 0)
        {
            return;
        }

        var firstCaller = new Dictionary<IMethodSymbol, (MethodDeclarationSyntax Caller, int Position)>(SymbolEqualityComparer.Default);
        foreach (var caller in methods)
        {
            var self = context.SemanticModel.GetDeclaredSymbol(caller, context.CancellationToken);
            foreach (var call in caller.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => privateNames.Contains(CalleeName(c))))
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

        foreach (var helper in methods)
        {
            if (context.SemanticModel.GetDeclaredSymbol(helper, context.CancellationToken) is { } symbol
                && firstCaller.TryGetValue(symbol, out var caller) && helper.SpanStart < caller.Position)
            {
                context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.HelperAboveCaller, helper.Identifier.GetLocation(), symbol.Name, caller.Caller.Identifier.ValueText));
            }
        }
    }

    private static string CalleeName(InvocationExpressionSyntax call) => call.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var name } => name.Identifier.ValueText,
        _ => string.Empty,
    };

    private sealed class State(IAssemblySymbol assembly)
    {
        private readonly ConcurrentDictionary<INamedTypeSymbol, bool> _core = new(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<ITypeSymbol, bool> _behavioral = new(SymbolEqualityComparer.Default);

        public void AnalyzeMethod(SyntaxNodeAnalysisContext context)
        {
            var declaration = (MethodDeclarationSyntax)context.Node;
            if (context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } method || method.IsOverride)
            {
                return;
            }

            var isHandler = method.DeclaredAccessibility == Accessibility.Public && IsInputHandler(method);
            var isCore = IsCore(context.Options, declaration.SyntaxTree, method.ContainingType);
            if (!isHandler && !isCore)
            {
                return;
            }

            var statements = Flatten(declaration.Body?.Statements ?? default)
                .Where(s => !LowLevel.IsGuard(s))
                .Select(s => (Statement: s, Kind: LowLevel.Find(s, context.SemanticModel)))
                .ToList();
            if (isHandler)
            {
                CheckHandler(context, declaration, method, statements);
            }

            if (isCore)
            {
                CheckMixed(context, declaration, method, statements);
            }
        }

        public void AnalyzeChain(SyntaxNodeAnalysisContext context)
        {
            var access = (MemberAccessExpressionSyntax)context.Node;
            SyntaxNode node = access.Parent is InvocationExpressionSyntax call && call.Expression == access ? call : access;
            if (node.Parent is MemberAccessExpressionSyntax parent && parent.Expression == node)
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

            var containing = context.ContainingSymbol?.ContainingType;
            if (steps.Count <= 2 || containing is null || !IsCore(context.Options, access.SyntaxTree, containing)
                || context.SemanticModel.GetSymbolInfo(current, context.CancellationToken).Symbol is INamespaceOrTypeSymbol)
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

                if (ResultType(symbol) is { } result && _behavioral.GetOrAdd(result, IsBehavioral))
                {
                    strangers++;
                }
            }

            if (strangers >= 2)
            {
                context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.LawOfDemeter, access.GetLocation(), access.ToString().Split('(')[0], strangers));
            }
        }

        private static void CheckHandler(SyntaxNodeAnalysisContext context, MethodDeclarationSyntax declaration, IMethodSymbol method, List<(StatementSyntax Statement, string? Kind)> statements)
        {
            var kind = statements.Select(s => s.Kind).FirstOrDefault(k => k is not null);
            if (kind is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.HandlerDoesWork, declaration.Identifier.GetLocation(), method.Name, kind));
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

        private bool IsCore(AnalyzerOptions options, SyntaxTree tree, INamedTypeSymbol type) =>
            _core.GetOrAdd(type, t => Settings.IsCore(options, tree, t));

        private bool IsBehavioral(ITypeSymbol type) =>
            type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface, IsRecord: false } named
            && !IsFramework(named)
            && SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly)
            && named.GetMembers().OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared && m.Name is not ("Equals" or "GetHashCode" or "ToString"));

        private void CheckMixed(SyntaxNodeAnalysisContext context, MethodDeclarationSyntax declaration, IMethodSymbol method, List<(StatementSyntax Statement, string? Kind)> statements)
        {
            var tree = declaration.SyntaxTree;
            var low = statements.Count(s => s.Kind is not null);
            var own = statements.Count(s => s.Kind is null && IsOwnCall(s.Statement, context.SemanticModel));
            var composer = Thresholds.Get(context.Options, tree, "qg_composer_percent", ReadabilityDescriptors.ComposerPercent);
            var worker = Thresholds.Get(context.Options, tree, "qg_worker_percent", ReadabilityDescriptors.WorkerPercent);
            var minOwn = Thresholds.Get(context.Options, tree, "qg_min_own_statements", ReadabilityDescriptors.MinOwnStatements);
            var minLow = Thresholds.Get(context.Options, tree, "qg_min_low_statements", ReadabilityDescriptors.MinLowStatements);
            if (statements.Count == 0 || own < minOwn || low < minLow)
            {
                return;
            }

            var percent = own * 100 / statements.Count;
            if (percent < composer && percent > worker)
            {
                context.ReportDiagnostic(Diagnostic.Create(ReadabilityDescriptors.MixedAbstraction, declaration.Identifier.GetLocation(), method.Name, own, low, percent));
            }
        }

        private bool IsOwnCall(SyntaxNode statement, SemanticModel model) =>
            statement.DescendantNodesAndSelf(n => n is not LocalFunctionStatementSyntax).Any(n =>
                n is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
                && model.GetSymbolInfo(n).Symbol is IMethodSymbol called
                && SymbolEqualityComparer.Default.Equals(called.ContainingAssembly, assembly));
    }
}
