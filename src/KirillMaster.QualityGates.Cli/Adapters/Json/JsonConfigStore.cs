using System.Text.Json;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Adapters.Json;

public sealed class JsonConfigStore : IConfigStore
{
    private sealed class FieldException(string path, string message) : Exception(message)
    {
        public string Path { get; } = path;
    }

    public ConfigLoadResult Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConfigLoadResult.Fail("$", $"cannot read '{path}': {ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return ConfigLoadResult.Ok(Parse(doc.RootElement));
        }
        catch (JsonException ex)
        {
            return ConfigLoadResult.Fail("$", $"invalid JSON: {ex.Message}");
        }
        catch (FieldException ex)
        {
            return ConfigLoadResult.Fail(ex.Path, ex.Message);
        }
    }

    private static GatesConfig Parse(JsonElement root)
    {
        Expect(root, JsonValueKind.Object, "$", "object");

        return new GatesConfig(
            Str(root, "service", "service", string.Empty) ?? string.Empty,
            ParseDiffCoverage(root),
            ParseMutation(root),
            ParseLayerOverrides(root),
            ParseIntegrationEnabled(root),
            ParseNotifications(root));
    }

    private static DiffCoverageThresholds ParseDiffCoverage(JsonElement root)
    {
        var diff = GatesConfig.Default.DiffCoverage;
        if (Child(root, "diffCoverage") is not { } d)
        {
            return diff;
        }

        Expect(d, JsonValueKind.Object, "diffCoverage", "object");
        return new DiffCoverageThresholds(
            Num(d, "diffCoverage.core", "core", diff.Core),
            Num(d, "diffCoverage.adapter", "adapter", diff.Adapter));
    }

    private static MutationSettings ParseMutation(JsonElement root)
    {
        var mutation = GatesConfig.Default.Mutation;
        if (Child(root, "mutation") is not { } m)
        {
            return mutation;
        }

        Expect(m, JsonValueKind.Object, "mutation", "object");
        return new MutationSettings(
            Num(m, "mutation.coreThreshold", "coreThreshold", mutation.CoreThreshold),
            (int)Num(m, "mutation.diffBudgetMinutes", "diffBudgetMinutes", mutation.DiffBudgetMinutes),
            (int)Num(m, "mutation.nightlyBudgetMinutes", "nightlyBudgetMinutes", mutation.NightlyBudgetMinutes));
    }

    private static Dictionary<string, Layer> ParseLayerOverrides(JsonElement root)
    {
        var overrides = new Dictionary<string, Layer>();
        if (Child(root, "layerOverrides") is not { } lo)
        {
            return overrides;
        }

        Expect(lo, JsonValueKind.Object, "layerOverrides", "object");
        foreach (var prop in lo.EnumerateObject())
        {
            var p = $"layerOverrides.{prop.Name}";
            Expect(prop.Value, JsonValueKind.String, p, "string");
            if (!Enum.TryParse<Layer>(prop.Value.GetString(), true, out var layer))
            {
                throw new FieldException(p, "unknown layer (expected core, adapter, test or excluded)");
            }

            overrides[prop.Name] = layer;
        }

        return overrides;
    }

    private static bool ParseIntegrationEnabled(JsonElement root)
    {
        if (Child(root, "integrationTestsEnabled") is not { } ie)
        {
            return GatesConfig.Default.IntegrationTestsEnabled;
        }

        if (ie.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FieldException("integrationTestsEnabled", "expected a boolean");
        }

        return ie.GetBoolean();
    }

    private static NotificationSettings? ParseNotifications(JsonElement root)
    {
        if (Child(root, "notifications") is not { } n)
        {
            return null;
        }

        Expect(n, JsonValueKind.Object, "notifications", "object");
        return new NotificationSettings(
            Str(n, "notifications.slackChannel", "slackChannel", null),
            Str(n, "notifications.jiraTechDebtIssue", "jiraTechDebtIssue", null),
            Str(n, "notifications.s3Bucket", "s3Bucket", null),
            Str(n, "notifications.s3Prefix", "s3Prefix", null));
    }

    private static JsonElement? Child(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static void Expect(JsonElement e, JsonValueKind kind, string path, string what)
    {
        if (e.ValueKind != kind)
        {
            throw new FieldException(path, $"expected {what}");
        }
    }

    private static double Num(JsonElement obj, string path, string name, double fallback)
    {
        if (Child(obj, name) is not { } v)
        {
            return fallback;
        }

        if (v.ValueKind != JsonValueKind.Number)
        {
            throw new FieldException(path, "expected a number");
        }

        return v.GetDouble();
    }

    private static string? Str(JsonElement obj, string path, string name, string? fallback)
    {
        if (Child(obj, name) is not { } v)
        {
            return fallback;
        }

        if (v.ValueKind != JsonValueKind.String)
        {
            throw new FieldException(path, "expected a string");
        }

        return v.GetString();
    }
}
