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
            start.RegisterSyntaxNodeAction(c => AnalyzeUnusedLocal(c, roles), SyntaxKind.LocalDeclarationStatement);
            start.RegisterSyntaxNodeAction(c => AnalyzeThrows(c, roles), SyntaxKind.MethodDeclaration);
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
            || !IsEndpointType(method.ContainingType, roles, tree)
            || !roles.IsResult(Symbols.UnwrapTask(method.ReturnType), tree)
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

        foreach (var lambda in invocation.ArgumentList.Arguments.Select(a => a.Expression).OfType<AnonymousFunctionExpressionSyntax>())
        {
            if (context.SemanticModel.GetSymbolInfo(lambda, context.CancellationToken).Symbol is IMethodSymbol symbol
                && roles.IsResult(Symbols.UnwrapTask(symbol.ReturnType), lambda.SyntaxTree))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultReturnedFromEndpoint, lambda.GetLocation(), name, symbol.ReturnType.Name));
            }
        }
    }

    private static void AnalyzeDiscardedStatement(OperationAnalysisContext context, Roles roles)
    {
        var statement = (IExpressionStatementOperation)context.Operation;
        if (IsCall(statement.Operation) && IsRelevantResult(statement.Operation.Type, statement.Syntax, context.ContainingSymbol, roles))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultNotUsed, statement.Syntax.GetLocation(), "Returned Result is discarded; check IsFailure and return the error, or return the Result"));
        }
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context, Roles roles)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        if (assignment.Target is IDiscardOperation && IsCall(assignment.Value) && IsRelevantResult(assignment.Value.Type, assignment.Syntax, context.ContainingSymbol, roles))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultNotUsed, assignment.Syntax.GetLocation(), "Returned Result is assigned to a discard; check IsFailure and return the error, or return the Result"));
            return;
        }

        if (assignment.Target is IPropertyReferenceOperation { Property.Name: "Detail" } target
            && target.Property.ContainingType.Name.Contains("ProblemDetails")
            && LeaksException(assignment.Value))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.ProblemDetailsLeak, assignment.Syntax.GetLocation()));
        }
    }

    private static void AnalyzeUnusedLocal(SyntaxNodeAnalysisContext context, Roles roles)
    {
        var declaration = (LocalDeclarationStatementSyntax)context.Node;
        if (declaration.Parent is null || declaration.IsConst || roles.LayerOf(context.ContainingSymbol, declaration.SyntaxTree) == Layer.Test)
        {
            return;
        }

        foreach (var variable in declaration.Declaration.Variables)
        {
            if (context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is not ILocalSymbol local || !roles.IsResult(local.Type, declaration.SyntaxTree))
            {
                continue;
            }

            var used = declaration.Parent.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Any(n => n.Identifier.ValueText == local.Name && SymbolEqualityComparer.Default.Equals(context.SemanticModel.GetSymbolInfo(n, context.CancellationToken).Symbol, local));
            if (!used)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultNotUsed, variable.Identifier.GetLocation(), $"Result '{local.Name}' is never used; check IsFailure and return the error, or return the Result"));
            }
        }
    }

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
        AnalyzeProblemCall(context, invocation);
    }

    private static void AnalyzeFluentCall(OperationAnalysisContext context, Roles roles, IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        var tree = invocation.Syntax.SyntaxTree;
        var name = method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name.Substring(0, method.Name.Length - "Async".Length) : method.Name;
        if (!roles.Config.List(tree, "qg_fluent_result_methods", Descriptors.FluentResultMethods).Contains(name)
            || roles.LayerOf(context.ContainingSymbol, tree) == Layer.Test
            || roles.IsResult(Roles.TypeOf(context.ContainingSymbol), tree))
        {
            return;
        }

        var receiver = method.IsExtensionMethod && invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value : invocation.Instance;
        if (roles.IsResult(Symbols.UnwrapTask(receiver?.Type!), tree) || roles.IsResult(Symbols.UnwrapTask(method.ReturnType), tree))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.FluentResultChain, invocation.Syntax.GetLocation(), $"'{method.Name}' chains on a Result"));
        }
    }

    private static void AnalyzeProblemCall(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        if (invocation.TargetMethod.Name != "Problem")
        {
            return;
        }

        var detail = invocation.Arguments.FirstOrDefault(a => string.Equals(a.Parameter?.Name, "detail", StringComparison.Ordinal));
        if (detail is not null && LeaksException(detail.Value))
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
        var method = (MethodDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken);
        if (symbol is null || !roles.IsResult(Symbols.UnwrapTask(symbol.ReturnType), method.SyntaxTree)
            || roles.LayerOf(symbol, method.SyntaxTree) == Layer.Test)
        {
            return;
        }

        var allowed = roles.Config.List(method.SyntaxTree, "qg_result_allowed_throws", Descriptors.AllowedResultThrows);
        SyntaxNode? body = method.Body ?? (SyntaxNode?)method.ExpressionBody;
        foreach (var node in body?.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax) ?? [])
        {
            var thrown = node switch
            {
                ThrowStatementSyntax { Expression: { } e } => e,
                ThrowExpressionSyntax t => t.Expression,
                _ => null,
            };
            if (thrown is null)
            {
                continue;
            }

            var thrownName = context.SemanticModel.GetTypeInfo(thrown, context.CancellationToken).Type?.Name ?? thrown.ToString();
            if (!allowed.Contains(thrownName))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ResultMethodThrows, node.GetFirstToken().GetLocation(), symbol.Name, thrownName));
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

    private static bool IsEndpointType(INamedTypeSymbol type, Roles roles, SyntaxTree tree)
    {
        var suffixes = roles.Config.List(tree, "qg_endpoint_type_suffixes", Descriptors.EndpointTypeSuffixes);
        return Symbols.InheritsFrom(type, "Microsoft.AspNetCore.Mvc.ControllerBase")
            || type.GetAttributes().Any(a => a.AttributeClass?.Name == "ApiControllerAttribute")
            || suffixes.Any(s => type.Name.EndsWith(s, StringComparison.Ordinal));
    }

    private static bool LeaksException(IOperation value) =>
        value.DescendantsAndSelf().Any(op => op switch
        {
            IPropertyReferenceOperation property => IsException(property.Instance?.Type) && ExceptionDetails.Contains(property.Property.Name),
            IInvocationOperation call => call.TargetMethod.Name == "ToString" && IsException(call.Instance?.Type),
            IInterpolationOperation interpolation => IsException(interpolation.Expression.Type),
            _ => false,
        });

    private static bool IsException(ITypeSymbol? type) => Symbols.InheritsFrom(type, "System.Exception");
}
