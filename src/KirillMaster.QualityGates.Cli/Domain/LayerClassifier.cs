using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain;

public sealed record Classification(Layer Layer, bool Unclassified);

public static class LayerClassifier
{
    private static readonly string[] TestSuffixes = [".Tests", ".IntegrationTests", ".UnitTests"];
    private static readonly string[] CoreSuffixes = [".Domain", ".Application"];

    public static Classification Classify(
        string projectName,
        string? projectPath = null,
        IReadOnlyDictionary<string, Layer>? overrides = null)
    {
        if (overrides is not null && overrides.TryGetValue(projectName, out var forced))
        {
            return new Classification(forced, false);
        }

        if (EndsWithAny(projectName, TestSuffixes))
        {
            return new Classification(Layer.Test, false);
        }

        if (EndsWithAny(projectName, CoreSuffixes) || InCoreFolder(projectPath))
        {
            return new Classification(Layer.Core, false);
        }

        return new Classification(Layer.Adapter, true);
    }

    public static bool IsTestName(string name) => EndsWithAny(name, TestSuffixes);

    private static bool EndsWithAny(string name, string[] suffixes) =>
        suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static bool InCoreFolder(string? path) =>
        path is not null && ("/" + path.Replace('\\', '/')).Contains("/Core/", StringComparison.OrdinalIgnoreCase);
}
