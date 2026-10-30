namespace KirillMaster.QualityGates.Analyzers;

internal enum Layer
{
    Unknown,
    Core,
    Adapter,
    Test,
}

internal sealed class Roles
{
    public const string TestSegments = "Tests,Test,UnitTests,IntegrationTests";
    public const string CoreSegments = "Core,Domain,Application,Ports";
    public const string AdapterSegments = "Adapters,Adapter,Runner,Runners,Infrastructure";
    public const string ConsumerInterfaces = "MassTransit.IConsumer";
    public const string ResultNames = "Result";
    public const string ErrorNames = "Error";
    public const string BoundarySuffixes = "ExceptionHandler,Middleware,ExceptionFilter";

    private const string LayerProperty = "build_property.QualityGatesLayer";

    private readonly CompilationConfig _config;
    private readonly string _assembly;

    public Roles(CompilationConfig config, Compilation compilation)
    {
        _config = config;
        _assembly = compilation.AssemblyName ?? string.Empty;
    }

    public CompilationConfig Config => _config;

    public static INamedTypeSymbol? TypeOf(ISymbol? symbol) => symbol as INamedTypeSymbol ?? symbol?.ContainingType;

    public Layer LayerOf(ISymbol? symbol, SyntaxTree tree)
    {
        _config.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(LayerProperty, out var property);
        if (property is "test" or "excluded")
        {
            return Layer.Test;
        }

        var tests = _config.List(tree, "qg_test_namespace_segments", TestSegments);
        var core = _config.List(tree, "qg_core_namespace_segments", CoreSegments);
        var adapter = _config.List(tree, "qg_adapter_namespace_segments", AdapterSegments);
        return Match(symbol?.ContainingNamespace?.ToDisplayString() ?? string.Empty, tests, core, adapter)
            ?? Match(_assembly, tests, core, adapter)
            ?? property switch { "core" => Layer.Core, "adapter" => Layer.Adapter, _ => Layer.Unknown };
    }

    public bool IsResult(ITypeSymbol? type, SyntaxTree tree) => IsNamed(type, tree, "qg_result_type_names", ResultNames);

    public bool IsError(ITypeSymbol? type, SyntaxTree tree) => IsNamed(type, tree, "qg_error_type_names", ErrorNames);

    public bool IsConsumer(INamedTypeSymbol? type, SyntaxTree tree)
    {
        var names = _config.List(tree, "qg_consumer_interfaces", ConsumerInterfaces);
        return type is not null && type.AllInterfaces.Any(i =>
            names.Contains(i.OriginalDefinition.Name) || names.Contains(i.OriginalDefinition.ContainingNamespace.ToDisplayString() + "." + i.OriginalDefinition.Name));
    }

    public bool IsBoundary(INamedTypeSymbol? type, SyntaxNode node)
    {
        var suffixes = _config.List(node.SyntaxTree, "qg_exception_boundary_suffixes", BoundarySuffixes);
        return _config.IsCompositionRoot(node)
            || (type is not null && (type.AllInterfaces.Any(i => i.Name == "IExceptionHandler")
                || suffixes.Any(s => type.Name.EndsWith(s, StringComparison.Ordinal))));
    }

    public static bool IsHostedService(INamedTypeSymbol? type) =>
        type is not null
        && (type.AllInterfaces.Any(i => i.Name == "IHostedService") || BaseTypes(type).Any(b => b.Name == "BackgroundService"));

    private static System.Collections.Generic.IEnumerable<INamedTypeSymbol> BaseTypes(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static Layer? Match(string dotted, ImmutableHashSet<string> tests, ImmutableHashSet<string> core, ImmutableHashSet<string> adapter)
    {
        var segments = dotted.Split('.');
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (tests.Contains(segments[i]))
            {
                return Layer.Test;
            }

            if (adapter.Contains(segments[i]))
            {
                return Layer.Adapter;
            }

            if (core.Contains(segments[i]))
            {
                return Layer.Core;
            }
        }

        return null;
    }

    private bool IsNamed(ITypeSymbol? type, SyntaxTree tree, string key, string fallback) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, TypeArguments.Length: <= 1 } named
        && _config.List(tree, key, fallback).Contains(named.Name);
}
