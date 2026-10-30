using System.Collections.Concurrent;

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
    public const string ResultNames = "KirillMaster.QualityGates.Results.Result";
    public const string ErrorNames = "KirillMaster.QualityGates.Results.Error";
    public const string BoundarySuffixes = "ExceptionHandler,Middleware,ExceptionFilter";

    private const string LayerProperty = "build_property.QualityGatesLayer";

    private readonly CompilationConfig _config;
    private readonly string _assembly;
    private readonly string? _property;
    private readonly INamedTypeSymbol? _argumentException;
    private readonly INamedTypeSymbol? _operationCanceled;
    private readonly INamedTypeSymbol? _exception;
    private readonly ConcurrentDictionary<(SyntaxTree, string), Layer> _layers = new();
    private readonly ConcurrentDictionary<(INamedTypeSymbol, bool), bool> _typeNames = new(TypeKeyComparer.Instance);
    private readonly ConcurrentDictionary<INamedTypeSymbol, bool> _hosted = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<INamedTypeSymbol, bool> _consumers = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<INamedTypeSymbol, bool> _controllers = new(SymbolEqualityComparer.Default);

    public Roles(CompilationConfig config, Compilation compilation)
    {
        _config = config;
        _assembly = compilation.AssemblyName ?? string.Empty;
        config.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(LayerProperty, out _property);
        _argumentException = compilation.GetTypeByMetadataName("System.ArgumentException");
        _operationCanceled = compilation.GetTypeByMetadataName("System.OperationCanceledException");
        _exception = compilation.GetTypeByMetadataName("System.Exception");
    }

    public CompilationConfig Config => _config;

    public static INamedTypeSymbol? TypeOf(ISymbol? symbol) => symbol as INamedTypeSymbol ?? symbol?.ContainingType;

    public Layer LayerOf(ISymbol? symbol, SyntaxTree tree)
    {
        if (_property is "test" or "excluded")
        {
            return Layer.Test;
        }

        var ns = symbol?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return _layers.GetOrAdd((tree, ns), key =>
        {
            var tests = _config.List(tree, "qg_test_namespace_segments", TestSegments);
            var core = _config.List(tree, "qg_core_namespace_segments", CoreSegments);
            var adapter = _config.List(tree, "qg_adapter_namespace_segments", AdapterSegments);
            return Match(key.Item2, tests, core, adapter)
                ?? Match(_assembly, tests, core, adapter)
                ?? _property switch { "core" => Layer.Core, "adapter" => Layer.Adapter, _ => Layer.Unknown };
        });
    }

    public bool IsResult(ITypeSymbol? type, SyntaxTree tree) => IsNamed(type, tree, "qg_result_type_names", ResultNames, false);

    public bool IsError(ITypeSymbol? type, SyntaxTree tree) => IsNamed(type, tree, "qg_error_type_names", ErrorNames, true);

    public bool IsConsumer(INamedTypeSymbol? type, SyntaxTree tree)
    {
        if (type is null)
        {
            return false;
        }

        return _consumers.GetOrAdd(type, t =>
        {
            var names = _config.List(tree, "qg_consumer_interfaces", ConsumerInterfaces);
            return t.AllInterfaces.Any(i =>
                names.Contains(i.OriginalDefinition.Name) || names.Contains(i.OriginalDefinition.ContainingNamespace.ToDisplayString() + "." + i.OriginalDefinition.Name));
        });
    }

    public bool IsBoundary(INamedTypeSymbol? type, SyntaxNode node)
    {
        if (_config.IsCompositionRoot(node))
        {
            return true;
        }

        if (type is null)
        {
            return false;
        }

        var suffixes = _config.List(node.SyntaxTree, "qg_exception_boundary_suffixes", BoundarySuffixes);
        return IsHostedService(type)
            || type.AllInterfaces.Any(i => i.Name == "IExceptionHandler")
            || suffixes.Any(s => type.Name.EndsWith(s, StringComparison.Ordinal));
    }

    public bool IsHostedService(INamedTypeSymbol? type) =>
        type is not null
        && _hosted.GetOrAdd(type, t => t.AllInterfaces.Any(i => i.Name == "IHostedService") || BaseTypes(t).Any(b => b.Name == "BackgroundService"));

    public bool IsControllerType(INamedTypeSymbol type) =>
        _controllers.GetOrAdd(type, t => BaseTypes(t).Any(b => b.ToDisplayString() == "Microsoft.AspNetCore.Mvc.ControllerBase")
            || t.GetAttributes().Any(a => a.AttributeClass?.Name == "ApiControllerAttribute"));

    public bool IsCancellation(ITypeSymbol? type) => Derives(type, _operationCanceled);

    public bool IsException(ITypeSymbol? type) => Derives(type, _exception);

    public bool IsArgumentException(ITypeSymbol? type) => Derives(type, _argumentException);

    private static bool Derives(ITypeSymbol? type, INamedTypeSymbol? baseType)
    {
        for (; baseType is not null && type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, baseType))
            {
                return true;
            }
        }

        return false;
    }

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

    private bool IsNamed(ITypeSymbol? type, SyntaxTree tree, string key, string defaults, bool error)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, TypeArguments.Length: <= 1 } named)
        {
            return false;
        }

        return _typeNames.GetOrAdd((named.OriginalDefinition, error), k =>
        {
            var extra = _config.List(tree, key, string.Empty);
            var ns = k.Item1.ContainingNamespace;
            var full = ns is null || ns.IsGlobalNamespace ? k.Item1.Name : ns.ToDisplayString() + "." + k.Item1.Name;
            return defaults == full || extra.Contains(full);
        });
    }

    private sealed class TypeKeyComparer : System.Collections.Generic.IEqualityComparer<(INamedTypeSymbol, bool)>
    {
        public static readonly TypeKeyComparer Instance = new();

        public bool Equals((INamedTypeSymbol, bool) x, (INamedTypeSymbol, bool) y) =>
            x.Item2 == y.Item2 && SymbolEqualityComparer.Default.Equals(x.Item1, y.Item1);

        public int GetHashCode((INamedTypeSymbol, bool) obj) => SymbolEqualityComparer.Default.GetHashCode(obj.Item1) ^ (obj.Item2 ? 1 : 0);
    }
}
