using System.Collections.Generic;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UsageAnalyzer : QgAnalyzer
{
    private static readonly ImmutableHashSet<string> MutatingCalls = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Add", "AddRange", "Remove", "RemoveAt", "RemoveAll", "Clear", "Push", "Pop", "Enqueue", "Dequeue", "Insert", "TryAdd", "TryRemove");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ShapeDescriptors.EnumSwitchOutsideOwner, ShapeDescriptors.OptionsWithoutValidateOnStart, ShapeDescriptors.MoneyNotDecimal,
        ShapeDescriptors.RoundWithoutMidpoint, ShapeDescriptors.CommandQuerySeparation);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var config = new CompilationConfig(start.Options, start.Compilation);
            var roles = new ShapeRoles(config, start.Compilation);
            if (roles.Skip)
            {
                return;
            }

            start.RegisterSyntaxNodeAction(c => AnalyzeSwitch(c, roles), SyntaxKind.SwitchStatement, SyntaxKind.SwitchExpression);
            start.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            start.RegisterSyntaxNodeAction(c => AnalyzeMethod(c, config, roles), SyntaxKind.MethodDeclaration);
            start.RegisterSymbolAction(c => AnalyzeMoney(c, roles), SymbolKind.Property, SymbolKind.Field, SymbolKind.Method);
        });

    private static void AnalyzeSwitch(SyntaxNodeAnalysisContext context, ShapeRoles roles)
    {
        var governing = context.Node switch
        {
            SwitchStatementSyntax s => s.Expression,
            SwitchExpressionSyntax e => e.GoverningExpression,
            _ => null,
        };
        var enumType = governing is null ? null : context.SemanticModel.GetTypeInfo(governing, context.CancellationToken).Type;
        var current = context.ContainingSymbol?.ContainingType;
        if (enumType is not { TypeKind: TypeKind.Enum } || current is null
            || !SymbolEqualityComparer.Default.Equals(enumType.ContainingAssembly, context.Compilation.Assembly)
            || roles.IsAdapter(current) || current.ContainingNamespace.IsGlobalNamespace
            || roles.InSegments(current, "qg_enum_switch_exempt_segments", ShapeDescriptors.EnumSwitchExemptSegments))
        {
            return;
        }

        if (roles.InSegments(enumType, "qg_enum_shared_segments", ShapeDescriptors.EnumSharedSegments))
        {
            return;
        }

        var owner = OwningFeature(enumType, roles, context.Compilation.AssemblyName ?? string.Empty);
        var where = current.ContainingNamespace.ToDisplayString();
        if (owner.Length > 0 && where != owner && !where.StartsWith(owner + ".", StringComparison.Ordinal))
        {
            context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.EnumSwitchOutsideOwner, governing!.GetLocation(), enumType.Name, owner));
        }
    }

    private static string OwningFeature(ITypeSymbol enumType, ShapeRoles roles, string assembly)
    {
        var full = enumType.ContainingNamespace.ToDisplayString();
        if (assembly.Length > 0 && full.StartsWith(assembly + ".", StringComparison.Ordinal))
        {
            return assembly + "." + full.Substring(assembly.Length + 1).Split('.')[0];
        }

        var segments = enumType.ContainingNamespace.ToDisplayString().Split('.').ToList();
        var structural = roles.List(enumType, "qg_model_namespace_segments", ShapeDescriptors.ModelSegments)
            .Union(roles.List(enumType, "qg_contract_namespace_segments", ShapeDescriptors.ContractSegments));
        if (segments.Count > 1 && structural.Contains(segments[segments.Count - 1]))
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return string.Join(".", segments);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var called = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            SimpleNameSyntax name => name.Identifier.ValueText,
            _ => string.Empty,
        };
        if (called is not ("Round" or "AddOptions" or "Configure"))
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
        {
            return;
        }

        if (method.Name == "Round" && method.ContainingType.ToDisplayString() is "System.Math" or "System.MathF" or "decimal" or "System.Decimal"
            && !method.Parameters.Any(p => p.Type.Name == "MidpointRounding"))
        {
            context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.RoundWithoutMidpoint, invocation.GetLocation(), method.ContainingType.Name + ".Round"));
            return;
        }

        if (!IsOptionsRegistration(method, context.Compilation))
        {
            return;
        }

        if (method.Name == "Configure" || !HasValidateOnStart(invocation))
        {
            var name = invocation.Expression is MemberAccessExpressionSyntax access ? access.Name.GetLocation() : invocation.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.OptionsWithoutValidateOnStart, name, method.Name));
        }
    }

    private static bool IsOptionsRegistration(IMethodSymbol method, Compilation compilation)
    {
        var reduced = method.ReducedFrom ?? method;
        return method.Name is "AddOptions" or "Configure"
            && method.TypeArguments.Length > 0
            && reduced.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.DependencyInjection"
            && reduced.Parameters.Length > 0 && reduced.Parameters[0].Type.Name == "IServiceCollection"
            && SymbolEqualityComparer.Default.Equals(method.TypeArguments[0].ContainingAssembly, compilation.Assembly);
    }

    private static bool HasValidateOnStart(InvocationExpressionSyntax invocation)
    {
        SyntaxNode current = invocation;
        while (current.Parent is MemberAccessExpressionSyntax access && access.Parent is InvocationExpressionSyntax next)
        {
            if (access.Name.Identifier.ValueText == "ValidateOnStart")
            {
                return true;
            }

            current = next;
        }

        if (current.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator })
        {
            return false;
        }

        var scope = declarator.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        return scope is not null && scope.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Any(a => a.Name.Identifier.ValueText == "ValidateOnStart" && a.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == declarator.Identifier.ValueText);
    }

    private static void AnalyzeMoney(SymbolAnalysisContext context, ShapeRoles roles)
    {
        var symbol = context.Symbol;
        if (symbol.IsImplicitlyDeclared || symbol.IsOverride || !symbol.Locations.Any(l => l.IsInSource) || Symbols.ImplementsInterface(symbol))
        {
            return;
        }

        var tree = symbol.Locations.First(l => l.IsInSource).SourceTree!;
        var pattern = Patterns.Option(context.Options, tree, "qg_money_name_pattern", ShapeDescriptors.MoneyNamePattern);
        var candidates = symbol switch
        {
            IPropertySymbol p when p.Locations.Length > 0 => new[] { (p.Name, p.Type, p.Locations[0]) },
            IFieldSymbol f when f.Locations.Length > 0 => new[] { (f.Name, f.Type, f.Locations[0]) },
            IMethodSymbol m when m.MethodKind == MethodKind.Ordinary => m.Parameters.Where(p => p.Locations.Length > 0).Select(p => (p.Name, p.Type, p.Locations[0])).ToArray(),
            _ => Array.Empty<(string, ITypeSymbol, Location)>(),
        };
        foreach (var (name, type, location) in candidates)
        {
            var floating = TypeNames.Element(type).SpecialType is SpecialType.System_Double or SpecialType.System_Single;
            if (floating && type is not IArrayTypeSymbol && Patterns.IsMatch(pattern, name))
            {
                context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.MoneyNotDecimal, location, name, type.ToDisplayString()));
            }
        }
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context, CompilationConfig config, ShapeRoles roles)
    {
        var syntax = (MethodDeclarationSyntax)context.Node;
        var body = (SyntaxNode?)syntax.Body ?? syntax.ExpressionBody;
        if (body is null || config.IsCompositionRoot(syntax) || context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not { } method
            || method.IsStatic || method.IsOverride || Symbols.ImplementsInterface(method) || !ReturnsValue(method))
        {
            return;
        }

        if (!roles.IsCore(method.ContainingType) && !roles.IsModel(method.ContainingType))
        {
            return;
        }

        var exempt = roles.List(method, "qg_cqs_exempt_prefixes", ShapeDescriptors.CqsExemptPrefixes);
        var results = roles.List(method, "qg_result_type_names", ShapeDescriptors.ResultTypeNames);
        var returned = Symbols.UnwrapTask(method.ReturnType);
        if (exempt.Any(p => method.Name.StartsWith(p, StringComparison.Ordinal)) || method.Name.Contains("OrCreate") || method.Name.Contains("OrAdd")
            || body.ToString().Contains("Interlocked.") || results.Contains(returned.Name)
            || SymbolEqualityComparer.Default.Equals(returned, method.ContainingType))
        {
            return;
        }

        var mutated = FirstMutation(body, context.SemanticModel, method.ContainingType, context.CancellationToken);
        if (mutated is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(ShapeDescriptors.CommandQuerySeparation, syntax.Identifier.GetLocation(), method.Name, mutated));
        }
    }

    private static bool ReturnsValue(IMethodSymbol method) =>
        method.MethodKind == MethodKind.Ordinary && !method.ReturnsVoid && !(TypeNames.IsTaskLike(method.ReturnType) && !((INamedTypeSymbol)method.ReturnType).IsGenericType);

    private static string? FirstMutation(SyntaxNode body, SemanticModel model, INamedTypeSymbol owner, System.Threading.CancellationToken token)
    {
        foreach (var node in body.DescendantNodes(n => n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)))
        {
            var target = node switch
            {
                AssignmentExpressionSyntax a when !a.IsKind(SyntaxKind.CoalesceAssignmentExpression) => a.Left,
                PostfixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.PostIncrementExpression) || p.IsKind(SyntaxKind.PostDecrementExpression) => p.Operand,
                PrefixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.PreIncrementExpression) || p.IsKind(SyntaxKind.PreDecrementExpression) => p.Operand,
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call } when MutatingCalls.Contains(call.Name.Identifier.ValueText) => call.Expression,
                _ => null,
            };
            if (target is ElementAccessExpressionSyntax element)
            {
                target = element.Expression;
            }

            if (target is not null && target.ToString().IndexOf("cache", StringComparison.OrdinalIgnoreCase) < 0 && IsOwnState(target, model, owner, token))
            {
                return target.ToString();
            }
        }

        return null;
    }

    private static bool IsOwnState(ExpressionSyntax target, SemanticModel model, INamedTypeSymbol owner, System.Threading.CancellationToken token)
    {
        var viaThis = target is IdentifierNameSyntax || target is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
        return viaThis && model.GetSymbolInfo(target, token).Symbol is IFieldSymbol or IPropertySymbol
            && model.GetSymbolInfo(target, token).Symbol is { IsStatic: false } symbol
            && owner.GetBaseTypesAndThis().Contains(symbol.ContainingType, SymbolEqualityComparer.Default);
    }
}

internal static class TypeExtensions
{
    public static IEnumerable<INamedTypeSymbol> GetBaseTypesAndThis(this INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }
}
