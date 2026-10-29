using System.Collections.Immutable;
using KirillMaster.QualityGates.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KirillMaster.QualityGates.Analyzers.Tests;

internal static class AnalyzerHarness
{
    private static readonly ImmutableArray<DiagnosticAnalyzer> All =
        [new SizeAnalyzer(), new HygieneAnalyzer(), new ErrorsAnalyzer(), new DesignAnalyzer(), new TestAnalyzer(), new StructureAnalyzer(), new CouplingAnalyzer(), new NamingAnalyzer()];

    private static readonly MetadataReference[] References =
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p))];

    public static Task<string[]> DiagnoseAsync(string source, params string[] enabledIds) =>
        DiagnoseAsync(source, new Dictionary<string, string>(), "Sample", string.Empty, enabledIds);

    public static Task<string[]> DiagnoseWithConfigAsync(string source, params (string Key, string Value)[] config) =>
        DiagnoseAsync(source, config.ToDictionary(c => c.Key, c => c.Value), "Sample", string.Empty);

    public static Task<string[]> DiagnoseInAssemblyAsync(string source, string assemblyName, string path = "", params (string Key, string Value)[] config) =>
        DiagnoseAsync(source, config.ToDictionary(c => c.Key, c => c.Value), assemblyName, path);

    private static async Task<string[]> DiagnoseAsync(string source, Dictionary<string, string> config, string assemblyName, string path, params string[] enabledIds)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path);
        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable,
            specificDiagnosticOptions: enabledIds.ToDictionary(id => id, _ => ReportDiagnostic.Warn));
        var compilation = CSharpCompilation.Create(assemblyName, [tree], References, options);
        var analyzerOptions = new AnalyzerOptions([], new ConfigProvider(config));
        var diagnostics = await compilation.WithAnalyzers(All, analyzerOptions).GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Select(d => d.Id).Order(StringComparer.Ordinal)];
    }

    public static IEnumerable<DiagnosticDescriptor> Descriptors() => All.SelectMany(a => a.SupportedDiagnostics);

    private sealed class ConfigProvider(Dictionary<string, string> values) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(values);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) =>
            values.TryGetValue(key, out value);
    }
}
