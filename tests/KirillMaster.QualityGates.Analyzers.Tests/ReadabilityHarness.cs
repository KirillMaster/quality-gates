using System.Collections.Immutable;
using KirillMaster.QualityGates.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace KirillMaster.QualityGates.Analyzers.Tests;

internal static class ReadabilityHarness
{
    private static readonly ImmutableArray<DiagnosticAnalyzer> All =
        [new TestQualityAnalyzer(), new CommentAnalyzer(), new IdentifierAnalyzer(), new AbstractionAnalyzer(), new NamingAnalyzer()];

    private static readonly MetadataReference[] References =
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p))];

    public static IEnumerable<DiagnosticDescriptor> Descriptors() => All.SelectMany(a => a.SupportedDiagnostics);

    public static async Task<string[]> DiagnoseAsync(string source, string assemblyName = "Sample", string? glossary = null, params (string Key, string Value)[] config)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        var compilation = CSharpCompilation.Create(assemblyName, [tree], References, options);
        var files = glossary is null ? [] : ImmutableArray.Create<AdditionalText>(new Glossary(glossary));
        var analyzerOptions = new AnalyzerOptions(files, new ConfigProvider(config.ToDictionary(c => c.Key, c => c.Value)));
        var diagnostics = await compilation.WithAnalyzers(All, analyzerOptions).GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Select(d => d.Id).Order(StringComparer.Ordinal)];
    }

    private sealed class Glossary(string text) : AdditionalText
    {
        public override string Path => "glossary.txt";

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

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
