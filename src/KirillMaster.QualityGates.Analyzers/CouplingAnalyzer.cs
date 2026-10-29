using System.Collections.Generic;
using Microsoft.CodeAnalysis.Operations;

namespace KirillMaster.QualityGates.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CouplingAnalyzer : QgAnalyzer
{
    private static readonly ImmutableHashSet<string> LocatorMethods = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "GetService", "GetRequiredService", "GetServices", "GetKeyedService", "GetKeyedServices", "GetRequiredKeyedService");

    private static readonly ImmutableHashSet<string> LocatorTypes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "System.IServiceProvider",
        "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider",
        "Microsoft.Extensions.DependencyInjection.ISupportRequiredService",
        "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions",
        "Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.MethodFanOut, Descriptors.NoServiceLocator);

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var config = new CompilationConfig(start.Options, start.Compilation);
            start.RegisterOperationBlockAction(c => AnalyzeFanOut(c, config));
            start.RegisterOperationAction(c => AnalyzeLocator(c, config), OperationKind.Invocation);
        });

    private static void AnalyzeFanOut(OperationBlockAnalysisContext context, CompilationConfig config)
    {
        if (context.OwningSymbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary, DeclaringSyntaxReferences.Length: > 0 } method)
        {
            return;
        }

        var syntax = method.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken);
        if (config.IsCompositionRoot(syntax))
        {
            return;
        }

        var ignored = config.List(syntax.SyntaxTree, "qg_fan_out_ignored_assemblies", Descriptors.FanOutIgnoredAssemblies);
        var callees = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var invocation in context.OperationBlocks.SelectMany(b => b.DescendantsAndSelf()).OfType<IInvocationOperation>())
        {
            var target = invocation.TargetMethod.OriginalDefinition;
            if (target.ContainingType is { } owner
                && !IsSameType(owner, method.ContainingType)
                && !IsIgnored(owner, ignored)
                && !IsData(owner))
            {
                callees.Add(target);
            }
        }

        var max = Thresholds.Get(context.Options, syntax.SyntaxTree, "qg_max_method_fan_out", Descriptors.MaxMethodFanOut);
        if (callees.Count > max)
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.MethodFanOut, method.Locations[0], method.Name, callees.Count, max));
        }
    }

    private static void AnalyzeLocator(OperationAnalysisContext context, CompilationConfig config)
    {
        var target = ((IInvocationOperation)context.Operation).TargetMethod;
        if (!LocatorMethods.Contains(target.Name)
            || target.ContainingType is null
            || !LocatorTypes.Contains(target.ContainingType.ToDisplayString())
            || config.IsCompositionRoot(context.Operation.Syntax))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Descriptors.NoServiceLocator, context.Operation.Syntax.GetLocation(), target.Name));
    }

    private static bool IsSameType(INamedTypeSymbol type, INamedTypeSymbol? current) =>
        current is not null && SymbolEqualityComparer.Default.Equals(Outermost(type).OriginalDefinition, Outermost(current).OriginalDefinition);

    private static INamedTypeSymbol Outermost(INamedTypeSymbol type)
    {
        while (type.ContainingType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    private static bool IsIgnored(INamedTypeSymbol type, ImmutableHashSet<string> prefixes)
    {
        var assembly = type.ContainingAssembly?.Name ?? string.Empty;
        return prefixes.Any(p => assembly.Equals(p, StringComparison.OrdinalIgnoreCase)
            || assembly.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsData(INamedTypeSymbol type) =>
        type.TypeKind is TypeKind.Enum or TypeKind.Delegate
        || type.IsRecord
        || (type.TypeKind is not TypeKind.Interface && !type.GetMembers().OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.Ordinary));
}
