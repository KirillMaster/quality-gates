using System.Text.Json;
using System.Text.RegularExpressions;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Adapters.Sarif;

public sealed record SarifResult(string RuleId, string Level, string? Uri, int Line, int Column, bool Suppressed);

public static partial class SarifReader
{
    [GeneratedRegex(@"\.(net\d+(\.\d+)?(-[a-z0-9.]+)?|netstandard\d+\.\d+|netcoreapp\d+\.\d+|net4\d+)$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TargetFrameworkSuffix();

    public static string ProjectName(string sarifPath)
    {
        var name = Path.GetFileName(sarifPath);
        if (name.EndsWith(".sarif", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".sarif".Length];
        }

        return TargetFrameworkSuffix().Replace(name, string.Empty);
    }

    public static IReadOnlyList<SarifResult> ReadResults(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var results = new List<SarifResult>();

        if (!document.RootElement.TryGetProperty("runs", out var runs))
        {
            return results;
        }

        foreach (var run in runs.EnumerateArray())
        {
            if (!run.TryGetProperty("results", out var items))
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                results.Add(ReadResult(item));
            }
        }

        return results;
    }

    /// <summary>Counts unsuppressed warning/error results per project and rule; target frameworks of one project merge by max.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Aggregate(IEnumerable<string> sarifPaths)
    {
        var projects = new SortedDictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        foreach (var path in sarifPaths)
        {
            var project = ProjectName(path);
            if (!projects.TryGetValue(project, out var merged))
            {
                merged = new Dictionary<string, int>(StringComparer.Ordinal);
                projects[project] = merged;
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var result in ReadResults(path))
            {
                if (result.Suppressed || !IsCounted(result.Level))
                {
                    continue;
                }

                counts[result.RuleId] = counts.GetValueOrDefault(result.RuleId) + 1;
            }

            foreach (var (rule, count) in counts)
            {
                if (count > merged.GetValueOrDefault(rule))
                {
                    merged[rule] = count;
                }
            }
        }

        var output = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (project, rules) in projects)
        {
            output[project] = rules;
        }

        return output;
    }

    /// <summary>Counted results with a location, de-duplicated per project/rule/uri/line (target frameworks of one project repeat them).</summary>
    public static IReadOnlyList<Finding> ReadFindings(IEnumerable<string> sarifPaths)
    {
        var seen = new HashSet<Finding>();
        var findings = new List<Finding>();
        foreach (var path in sarifPaths)
        {
            var project = ProjectName(path);
            foreach (var result in ReadResults(path))
            {
                if (result.Suppressed || !IsCounted(result.Level) || result.Uri is null || result.Line <= 0)
                {
                    continue;
                }

                var finding = new Finding(project, result.RuleId, result.Uri, result.Line);
                if (seen.Add(finding))
                {
                    findings.Add(finding);
                }
            }
        }

        return findings;
    }

    public static bool IsCounted(string level) =>
        string.Equals(level, "warning", StringComparison.OrdinalIgnoreCase)
        || string.Equals(level, "error", StringComparison.OrdinalIgnoreCase);

    private static SarifResult ReadResult(JsonElement item)
    {
        var ruleId = item.TryGetProperty("ruleId", out var rule) ? rule.GetString() ?? string.Empty : string.Empty;
        var level = item.TryGetProperty("level", out var lvl) ? lvl.GetString() ?? "warning" : "warning";
        var suppressed = item.TryGetProperty("suppressions", out var suppressions)
                         && suppressions.ValueKind == JsonValueKind.Array
                         && suppressions.GetArrayLength() > 0;

        string? uri = null;
        var line = 0;
        var column = 0;
        if (item.TryGetProperty("locations", out var locations)
            && locations.ValueKind == JsonValueKind.Array
            && locations.GetArrayLength() > 0
            && locations[0].TryGetProperty("physicalLocation", out var physical))
        {
            if (physical.TryGetProperty("artifactLocation", out var artifact) && artifact.TryGetProperty("uri", out var u))
            {
                uri = u.GetString();
            }

            if (physical.TryGetProperty("region", out var region))
            {
                line = region.TryGetProperty("startLine", out var l) ? l.GetInt32() : 0;
                column = region.TryGetProperty("startColumn", out var c) ? c.GetInt32() : 0;
            }
        }

        return new SarifResult(ruleId, level, uri, line, column, suppressed);
    }
}
