using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Analyzers;

internal sealed class ShapeRoles
{
    private readonly CompilationConfig _config;
    private readonly bool _coreLayer;

    public ShapeRoles(CompilationConfig config, Compilation compilation)
    {
        _config = config;
        var global = config.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
        var layer = global.TryGetValue("build_property.QualityGatesLayer", out var value) ? value.Trim() : string.Empty;
        _coreLayer = layer.Equals("core", StringComparison.OrdinalIgnoreCase);
        Skip = layer.Equals("test", StringComparison.OrdinalIgnoreCase)
            || layer.Equals("excluded", StringComparison.OrdinalIgnoreCase)
            || (compilation.AssemblyName ?? string.Empty).EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || (compilation.AssemblyName ?? string.Empty).EndsWith(".Test", StringComparison.OrdinalIgnoreCase);
    }

    public bool Skip { get; }

    public bool CoreLayer => _coreLayer;

    public bool IsPort(ISymbol type) => InSegments(type, "qg_port_namespace_segments", ShapeDescriptors.PortSegments);

    public bool IsModel(ISymbol type) => InSegments(type, "qg_model_namespace_segments", ShapeDescriptors.ModelSegments);

    public bool IsContract(ISymbol type) => InSegments(type, "qg_contract_namespace_segments", ShapeDescriptors.ContractSegments);

    public bool IsAdapter(ISymbol type) => InSegments(type, "qg_adapter_namespace_segments", ShapeDescriptors.AdapterSegments);

    public bool IsCore(ISymbol type) => !IsPort(type) && (_coreLayer || InSegments(type, "qg_core_namespace_segments", ShapeDescriptors.CoreSegments));

    public bool IsPortInterface(INamedTypeSymbol type) => type.TypeKind == TypeKind.Interface && IsPort(type);

    public ImmutableHashSet<string> List(ISymbol symbol, string key, string fallback)
    {
        var tree = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
        return tree is null
            ? fallback.Split(',').Select(v => v.Trim()).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
            : _config.List(tree, key, fallback);
    }

    public bool InSegments(ISymbol type, string key, string fallback)
    {
        var tree = type.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
        if (tree is null)
        {
            return false;
        }

        var segments = _config.List(tree, key, fallback);
        var ns = type.ContainingNamespace.ToDisplayString();
        var dotted = "." + ns + ".";
        return segments.Any(s => s.IndexOf('.') >= 0 ? dotted.IndexOf("." + s + ".", StringComparison.OrdinalIgnoreCase) >= 0 : ns.Split('.').Contains(s, StringComparer.OrdinalIgnoreCase));
    }
}

internal static class Patterns
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static bool IsMatch(string pattern, string input)
    {
        try
        {
            return Cache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1))).IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static string Option(AnalyzerOptions options, SyntaxTree tree, string key, string fallback) =>
        options.AnalyzerConfigOptionsProvider.GetOptions(tree).TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
}

internal static class TypeNames
{
    public static string Full(ITypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + type.Name : type.Name;

    public static bool IsTaskLike(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "Task" or "ValueTask" } && type.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";

    public static bool IsAsyncEnumerable(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "IAsyncEnumerable" } && type.ContainingNamespace.ToDisplayString() == "System.Collections.Generic";

    public static bool IsCancellationToken(ITypeSymbol type) => Full(type) == "System.Threading.CancellationToken";

    public static IEnumerable<ITypeSymbol> Flatten(ITypeSymbol type)
    {
        yield return type;
        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (var inner in Flatten(array.ElementType))
                {
                    yield return inner;
                }

                break;
            case INamedTypeSymbol { IsGenericType: true } named:
                foreach (var inner in named.TypeArguments.SelectMany(Flatten))
                {
                    yield return inner;
                }

                break;
        }
    }

    public static ITypeSymbol Element(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => Element(array.ElementType),
        INamedTypeSymbol { TypeArguments.Length: 1 } named => Element(named.TypeArguments[0]),
        _ => type,
    };
}
