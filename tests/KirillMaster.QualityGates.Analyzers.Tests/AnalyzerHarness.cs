using System.Collections.Immutable;
using KirillMaster.QualityGates.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KirillMaster.QualityGates.Analyzers.Tests;

internal static class AnalyzerHarness
{
    private static readonly ImmutableArray<DiagnosticAnalyzer> All =
        [new SizeAnalyzer(), new HygieneAnalyzer(), new ErrorsAnalyzer(), new DesignAnalyzer(), new TestAnalyzer()];

    private static readonly MetadataReference[] References =
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p))];

    public static async Task<string[]> DiagnoseAsync(string source, params string[] enabledIds)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable,
            specificDiagnosticOptions: enabledIds.ToDictionary(id => id, _ => ReportDiagnostic.Warn));
        var compilation = CSharpCompilation.Create("Sample", [tree], References, options);
        var diagnostics = await compilation.WithAnalyzers(All).GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Select(d => d.Id).Order(StringComparer.Ordinal)];
    }

    public static IEnumerable<DiagnosticDescriptor> Descriptors() => All.SelectMany(a => a.SupportedDiagnostics);
}
