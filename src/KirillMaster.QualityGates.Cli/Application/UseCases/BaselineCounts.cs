using System.Text.Json;
using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Cli.Application.UseCases;

/// <summary>Flattens a baseline file into key to count: numeric JSON leaves by path, string array items as count 1, and one count per SuppressMessage target in a GlobalSuppressions file. quality/baseline.json counts only its warnings, grandfathered and fanOut sections (coverage there is a floor, not debt).</summary>
public static partial class BaselineCounts
{
    public const string SuppressionsGroup = "GlobalSuppressions";
    public const string FanOutSection = "fanOut";

    public static bool IsSuppressionsFile(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, int> Parse(string path, string? content)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(content))
        {
            return counts;
        }

        if (IsSuppressionsFile(path))
        {
            foreach (var line in content.Split('\n').Where(l => l.Contains("SuppressMessage", StringComparison.Ordinal)))
            {
                var key = TargetPattern().Match(line) is { Success: true } target ? target.Groups[1].Value : line.Trim();
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }

            return counts;
        }

        using var document = JsonDocument.Parse(content);
        var sections = System.IO.Path.GetFileName(path).Equals("baseline.json", StringComparison.OrdinalIgnoreCase) ? new[] { "warnings", "grandfathered", FanOutSection } : null;
        Flatten(document.RootElement, string.Empty, sections, counts);
        return counts;
    }

    private static void Flatten(JsonElement element, string prefix, string[]? sections, Dictionary<string, int> counts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (prefix.Length == 0 && sections is not null && !sections.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Flatten(property.Value, prefix.Length == 0 ? property.Name : prefix + "/" + property.Name, null, counts);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var key = $"{prefix}[{item.GetString()}]";
                        counts[key] = counts.GetValueOrDefault(key) + 1;
                    }
                    else
                    {
                        Flatten(item, $"{prefix}[{index}]", null, counts);
                    }

                    index++;
                }

                break;
            case JsonValueKind.Number when element.TryGetInt32(out var number):
                counts[prefix] = number;
                break;
        }
    }

    [GeneratedRegex("Target\\s*=\\s*\"([^\"]*)\"")]
    private static partial Regex TargetPattern();
}
