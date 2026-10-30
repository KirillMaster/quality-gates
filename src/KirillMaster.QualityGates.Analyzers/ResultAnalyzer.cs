using System.Collections.Generic;
using Microsoft.CodeAnalysis.Operations;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ResultAnalyzer : QgAnalyzer
{
    private static readonly ImmutableHashSet<string> MinimalApiMaps = ImmutableHashSet.Create(
        StringComparer.Ordinal, "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods");

    private static readonly ImmutableHashSet<string> ExceptionDetails = ImmutableHashSet.Create(
        StringComparer.Ordinal, "Message", "StackTrace", "Source", "Data", "InnerException");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.ResultReturnedFromEndpoint, Descriptors.ResultNotUsed, Descriptors.ErrorLiteralCode,
        Descriptors.ResultMethodThrows, Descriptors.ResultNotNullAnnotated, Descriptors.ProblemDetailsLeak,
        Descriptors.FluentResultChain);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var roles = new Roles(new CompilationConfig(start.Options, start.Compilation), start.Compilation);
            start.RegisterSymbolAction(c => AnalyzeEndpointMethod(c, roles), SymbolKind.Method);
            start.RegisterSymbolAction(c => AnalyzeResultType(c, roles), SymbolKind.NamedType);
            start.RegisterSyntaxNodeAction(c => AnalyzeMinimalApi(c, roles), SyntaxKind.InvocationExpression);
            start.RegisterOperationBlockAction(c => AnalyzeUnusedLocals(c, roles));
            start.RegisterSyntaxNodeAction(c => AnalyzeThrows(c, roles), SyntaxKind.MethodDeclaration, SyntaxKind.LocalFunctionStatement, SyntaxKind.GetAccessorDeclaration, SyntaxKind.PropertyDeclaration);
            start.RegisterSyntaxNodeAction(c => AnalyzeQuery(c, roles), SyntaxKind.QueryExpression);
            start.RegisterOperationAction(c => AnalyzeDiscardedStatement(c, roles), OperationKind.ExpressionStatement);
            start.RegisterOperationAction(c => AnalyzeAssignment(c, roles), OperationKind.SimpleAssignment);
            start.RegisterOperationAction(c => AnalyzeCreation(c, roles), OperationKind.ObjectCreation);
            start.RegisterOperationAction(c => AnalyzeInvocation(c, roles), OperationKind.Invocation);
        });

    private static void AnalyzeEndpointMethod(SymbolAnalysisContext context, Roles roles)
    {
        var method = (IMethodSymbol)context.Symbol;
        var tree = method.Locations.FirstOrDefault()?.SourceTree;
        if (tree is null || method.MethodKind != MethodKind.Ordinary || method.DeclaredAccessibility != Accessibility.Public
            || !roles.IsResult(Symbols.UnwrapTask(method.ReturnType), tree)
            || !IsEndpointMethod(method, roles, tree)
            || roles.LayerOf(method, tree) == Layer.Test)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultReturnedFromEndpoint, method.Locations[0], method.Name, method.ReturnType.Name));
    }

    private static void AnalyzeMinimalApi(SyntaxNodeAnalysisContext context, Roles roles)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var name = (invocation.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText;
        if (name is null || !MinimalApiMaps.Contains(name) || roles.LayerOf(context.ContainingSymbol, invocation.SyntaxTree) == Layer.Test)
        {
            return;
        }

        foreach (var handler in invocation.ArgumentList.Arguments.Select(a => a.Expression))
        {
            var info = handler is AnonymousFunctionExpressionSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax
                ? context.SemanticModel.GetSymbolInfo(handler, context.CancellationToken)
                : default;
            var symbol = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (symbol is not null && roles.IsResult(Symbols.UnwrapTask(symbol.ReturnType), handler.SyntaxTree))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultReturnedFromEndpoint, handler.GetLocation(), name, symbol.ReturnType.Name));
            }
        }
    }

    private static void AnalyzeDiscardedStatement(OperationAnalysisContext context, Roles roles)
    {
        var statement = (IExpressionStatementOperation)context.Operation;
        if (IsCall(statement.Operation) && IsRelevantResult(Symbols.UnwrapTask(statement.Operation.Type!), statement.Syntax, context.ContainingSymbol, roles))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultNotUsed, statement.Syntax.GetLocation(), "Returned Result is discarded; check IsFailure and return the error, or return the Result"));
        }
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context, Roles roles)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        if (assignment.Target is IDiscardOperation && IsCall(assignment.Value) && IsRelevantResult(Symbols.UnwrapTask(assignment.Value.Type!), assignment.Syntax, context.ContainingSymbol, roles))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultNotUsed, assignment.Syntax.GetLocation(), "Returned Result is assigned to a discard; check IsFailure and return the error, or return the Result"));
            return;
        }

        if (assignment.Target is IPropertyReferenceOperation { Property.Name: "Detail" } target
            && target.Property.ContainingType.Name.Contains("ProblemDetails")
            && LeaksException(assignment.Value, roles, assignment.Syntax.SyntaxTree))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ProblemDetailsLeak, assignment.Syntax.GetLocation()));
        }
    }

    private static void AnalyzeUnusedLocals(OperationBlockAnalysisContext context, Roles roles)
    {
        var declared = new Dictionary<ILocalSymbol, Location>(SymbolEqualityComparer.Default);
        var read = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        foreach (var operation in context.OperationBlocks.SelectMany(b => b.DescendantsAndSelf()))
        {
            switch (operation)
            {
                case IVariableDeclaratorOperation declarator:
                    declared[declarator.Symbol] = declarator.Symbol.Locations[0];
                    break;
                case IDeclarationPatternOperation { DeclaredSymbol: ILocalSymbol pattern }:
                    declared[pattern] = pattern.Locations[0];
                    break;
                case ILocalReferenceOperation { IsDeclaration: true } declaration:
                    declared[declaration.Local] = declaration.Local.Locations[0];
                    break;
                case ILocalReferenceOperation reference when IsRead(reference):
                    read.Add(reference.Local);
                    break;
            }
        }

        foreach (var pair in declared)
        {
            var tree = pair.Value.SourceTree;
            if (tree is not null && !read.Contains(pair.Key) && roles.IsResult(pair.Key.Type, tree) && roles.LayerOf(context.OwningSymbol, tree) != Layer.Test)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultNotUsed, pair.Value, $"Result '{pair.Key.Name}' is never read; check IsFailure and return the error, or return the Result"));
            }
        }
    }

    private static bool IsRead(ILocalReferenceOperation reference) =>
        !(reference.Parent is ISimpleAssignmentOperation assignment && assignment.Target == reference);

    private static void AnalyzeCreation(OperationAnalysisContext context, Roles roles)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (roles.IsError(creation.Type, creation.Syntax.SyntaxTree))
        {
            ReportLiteralCode(context, roles, creation.Arguments);
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, Roles roles)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        var tree = invocation.Syntax.SyntaxTree;
        if (method.IsStatic && roles.IsError(method.ReturnType, tree) && roles.IsError(method.ContainingType, tree))
        {
            ReportLiteralCode(context, roles, invocation.Arguments);
        }

        AnalyzeFluentCall(context, roles, invocation);
        AnalyzeProblemCall(context, roles, invocation);
    }

    private static void AnalyzeFluentCall(OperationAnalysisContext context, Roles roles, IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        var tree = invocation.Syntax.SyntaxTree;
        var name = method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name.Substring(0, method.Name.Length - "Async".Length) : method.Name;
        if (!roles.Config.List(tree, "qg_fluent_result_methods", Descriptors.FluentResultMethods).Contains(name))
        {
            return;
        }

        var receiver = method.IsExtensionMethod && invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value : invocation.Instance;
        if (receiver?.Type is null || !roles.IsResult(Symbols.UnwrapTask(receiver.Type), tree)
            || roles.IsResult(Roles.TypeOf(context.ContainingSymbol), tree)
            || roles.LayerOf(context.ContainingSymbol, tree) == Layer.Test)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.FluentResultChain, invocation.Syntax.GetLocation(), $"'{method.Name}' chains on a Result"));
    }

    private static void AnalyzeProblemCall(OperationAnalysisContext context, Roles roles, IInvocationOperation invocation)
    {
        if (invocation.TargetMethod.Name != "Problem")
        {
            return;
        }

        var detail = invocation.Arguments.FirstOrDefault(a => string.Equals(a.Parameter?.Name, "detail", StringComparison.Ordinal));
        if (detail is not null && LeaksException(detail.Value, roles, detail.Syntax.SyntaxTree))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ProblemDetailsLeak, detail.Syntax.GetLocation()));
        }
    }

    private static void AnalyzeQuery(SyntaxNodeAnalysisContext context, Roles roles)
    {
        var query = (QueryExpressionSyntax)context.Node;
        if (roles.LayerOf(context.ContainingSymbol, query.SyntaxTree) == Layer.Test)
        {
            return;
        }

        var sources = new[] { query.FromClause }.Concat(query.Body.Clauses.OfType<FromClauseSyntax>());
        if (sources.Any(f => roles.IsResult(Symbols.UnwrapTask(context.SemanticModel.GetTypeInfo(f.Expression, context.CancellationToken).Type!), query.SyntaxTree)))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.FluentResultChain, query.FromClause.FromKeyword.GetLocation(), "LINQ query syntax over a Result"));
        }
    }

    private static void AnalyzeThrows(SyntaxNodeAnalysisContext context, Roles roles)
    {
        var node = context.Node;
        var returned = context.SemanticModel.GetDeclaredSymbol(node, context.CancellationToken) switch
        {
            IMethodSymbol method => method.ReturnType,
            IPropertySymbol property when node is PropertyDeclarationSyntax { ExpressionBody: not null } => property.Type,
            _ => null,
        };
        SyntaxNode? body = node switch
        {
            MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
            LocalFunctionStatementSyntax l => (SyntaxNode?)l.Body ?? l.ExpressionBody,
            AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
            PropertyDeclarationSyntax p => p.ExpressionBody,
            _ => null,
        };
        if (returned is null || body is null || !roles.IsResult(Symbols.UnwrapTask(returned), node.SyntaxTree)
            || roles.LayerOf(context.ContainingSymbol, node.SyntaxTree) == Layer.Test)
        {
            return;
        }

        var allowed = roles.Config.List(node.SyntaxTree, "qg_result_allowed_throws", Descriptors.AllowedResultThrows);
        foreach (var candidate in body.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax))
        {
            var thrown = candidate switch
            {
                ThrowStatementSyntax { Expression: { } e } => e,
                ThrowExpressionSyntax t => t.Expression,
                _ => null,
            };
            if (thrown is null)
            {
                continue;
            }

            var thrownType = context.SemanticModel.GetTypeInfo(thrown, context.CancellationToken).Type;
            var thrownName = thrownType?.Name ?? thrown.ToString();
            if (!allowed.Contains(thrownName) && !roles.IsArgumentException(thrownType))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultMethodThrows, candidate.GetFirstToken().GetLocation(), context.ContainingSymbol?.Name, thrownName));
            }
        }
    }

    private static void AnalyzeResultType(SymbolAnalysisContext context, Roles roles)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var tree = type.Locations.FirstOrDefault()?.SourceTree;
        if (tree is null || !roles.IsResult(type, tree))
        {
            return;
        }

        var carriers = type.GetMembers().OfType<IPropertySymbol>().Where(p => p.Name is "Value" or "Error" && !(p.Type.IsValueType && p.Type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)).ToArray();
        if (carriers.Length == 0)
        {
            return;
        }

        foreach (var flag in type.GetMembers().OfType<IPropertySymbol>().Where(p => p.Name is "IsSuccess" or "IsFailure"))
        {
            if (!flag.GetAttributes().Any(a => a.AttributeClass?.Name == "MemberNotNullWhenAttribute"))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultNotNullAnnotated, flag.Locations[0], type.Name, flag.Name));
            }
        }
    }

    private static void ReportLiteralCode(OperationAnalysisContext context, Roles roles, ImmutableArray<IArgumentOperation> arguments)
    {
        var tree = context.Operation.Syntax.SyntaxTree;
        var code = arguments.FirstOrDefault(a => string.Equals(a.Parameter?.Name, "code", StringComparison.OrdinalIgnoreCase))
            ?? arguments.FirstOrDefault(a => a.Parameter?.Type.SpecialType == SpecialType.System_String);
        if (code is null || !IsLiteral(code.Value) || roles.LayerOf(context.ContainingSymbol, tree) == Layer.Test || InCatalog(context.ContainingSymbol, roles, tree))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.ErrorLiteralCode, code.Syntax.GetLocation()));
    }

    private static bool InCatalog(ISymbol? symbol, Roles roles, SyntaxTree tree)
    {
        var suffixes = roles.Config.List(tree, "qg_error_catalog_suffixes", Descriptors.ErrorCatalogSuffixes);
        for (var type = Roles.TypeOf(symbol); type is not null; type = type.ContainingType)
        {
            if ((type.IsStatic && suffixes.Any(s => type.Name.EndsWith(s, StringComparison.Ordinal))) || roles.IsError(type, tree))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLiteral(IOperation value)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        return value is ILiteralOperation or IInterpolatedStringOperation;
    }

    private static bool IsCall(IOperation operation) =>
        operation is IInvocationOperation || operation is IAwaitOperation { Operation: IInvocationOperation };

    private static bool IsRelevantResult(ITypeSymbol? type, SyntaxNode node, ISymbol? containing, Roles roles) =>
        roles.IsResult(type, node.SyntaxTree) && roles.LayerOf(containing, node.SyntaxTree) != Layer.Test;

    private static bool IsEndpointMethod(IMethodSymbol method, Roles roles, SyntaxTree tree)
    {
        var type = method.ContainingType;
        if (roles.IsControllerType(type))
        {
            return true;
        }

        var suffixes = roles.Config.List(tree, "qg_endpoint_type_suffixes", Descriptors.EndpointTypeSuffixes);
        return suffixes.Any(s => type.Name.EndsWith(s, StringComparison.Ordinal))
            && method.GetAttributes().Any(a => a.AttributeClass is { } c && (c.Name == "RouteAttribute" || (c.Name.StartsWith("Http", StringComparison.Ordinal) && c.Name.EndsWith("Attribute", StringComparison.Ordinal))));
    }

    private static bool LeaksException(IOperation value, Roles roles, SyntaxTree tree)
    {
        var safe = roles.Config.List(tree, "qg_problem_details_safe_exceptions", string.Empty);
        return value.DescendantsAndSelf().Any(op => op switch
        {
            IPropertyReferenceOperation property => IsLeakyException(property.Instance?.Type, roles, safe) && ExceptionDetails.Contains(property.Property.Name),
            IInvocationOperation call => call.TargetMethod.Name == "ToString" && IsLeakyException(call.Instance?.Type, roles, safe),
            IInterpolationOperation interpolation => IsLeakyException(interpolation.Expression.Type, roles, safe),
            _ => false,
        });
    }

    private static bool IsLeakyException(ITypeSymbol? type, Roles roles, ImmutableHashSet<string> safe) =>
        roles.IsException(type) && !safe.Contains(type!.Name) && !safe.Contains(type.ToDisplayString());
}
