using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Architecture.Contracts;

/// <summary>
/// H7/Y7 helper: snapshot and breaking-change diff of an OpenAPI 3 JSON document. Generic over how the document is obtained (WebApplicationFactory GET /openapi/v1.json, build-time generator),
/// so call it from a test class: <c>OpenApiSnapshot.Assert(json, "quality/openapi/v1.json")</c>.
/// </summary>
public static class OpenApiSnapshot
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    /// <summary>Document with sorted keys; <c>info.version</c> and <c>servers</c> (volatile, not part of the contract) are removed by default.</summary>
    public static string Normalize(string json, bool dropVolatile = true) => dropVolatile ? SnapshotStore.Normalize(json, "info.version", "servers") : SnapshotStore.Normalize(json);

    /// <summary>Fails when the generated document differs from the snapshot file (see <see cref="SnapshotStore"/> for the update switch).</summary>
    public static void Assert(string json, string snapshotPath, bool dropVolatile = true) =>
        SnapshotStore.Assert(Normalize(json, dropVolatile), snapshotPath, BreakingChanges);

    /// <summary>
    /// Breaking changes from <paramref name="oldJson"/> to <paramref name="newJson"/>: removed path, operation, success response or media type; new required parameter or request body;
    /// parameter made required; schema changes (removed property or enum value, changed type or reference, newly required property).
    /// </summary>
    public static IReadOnlyList<string> BreakingChanges(string oldJson, string newJson)
    {
        var old = JsonNode.Parse(oldJson) as JsonObject ?? [];
        var current = JsonNode.Parse(newJson) as JsonObject ?? [];
        var found = new List<string>();
        var newPaths = (current["paths"] as JsonObject ?? []).GroupBy(p => Template(p.Key), StringComparer.Ordinal).ToDictionary(g => g.Key, g => (Route: g.Last().Key, Item: g.Last().Value), StringComparer.Ordinal);
        foreach (var (route, oldItem) in old["paths"] as JsonObject ?? [])
        {
            if (!newPaths.TryGetValue(Template(route), out var target) || target.Item is not JsonObject item)
            {
                found.Add($"path {route} removed");
                continue;
            }

            foreach (var method in Methods.Where(m => oldItem?[m] is JsonObject))
            {
                var label = $"{method.ToUpperInvariant()} {route}";
                if (item[method] is JsonObject operation)
                {
                    CompareOperation(label, route, target.Route, oldItem as JsonObject ?? [], (JsonObject)oldItem![method]!, item, operation, found);
                }
                else
                {
                    found.Add($"operation {label} removed");
                }
            }
        }

        var newSchemas = current["components"]?["schemas"] as JsonObject ?? [];
        foreach (var (name, schema) in old["components"]?["schemas"] as JsonObject ?? [])
        {
            SchemaDiff.Compare("schema " + name, schema, newSchemas[name], found);
        }

        return found;
    }

    /// <summary>Route with parameter names erased, so renaming <c>{id}</c> to <c>{orderId}</c> keeps the same path.</summary>
    private static string Template(string route) => Regex.Replace(route, @"\{[^}]*\}", "{}", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static void CompareOperation(string label, string route, string newRoute, JsonObject oldItem, JsonObject old, JsonObject newItem, JsonObject current, List<string> found)
    {
        var oldParameters = Parameters(oldItem, old, route);
        foreach (var (key, parameter) in Parameters(newItem, current, newRoute))
        {
            var required = parameter["required"]?.GetValue<bool>() ?? false;
            if (!oldParameters.TryGetValue(key, out var before))
            {
                if (required)
                {
                    found.Add($"{label} gained required parameter {key}");
                }
            }
            else
            {
                if (required && !(before["required"]?.GetValue<bool>() ?? false))
                {
                    found.Add($"{label} parameter {key} became required");
                }

                SchemaDiff.Compare($"{label} parameter {key}", before["schema"], parameter["schema"], found, ContractDirection.Request);
            }
        }

        if ((current["requestBody"]?["required"]?.GetValue<bool>() ?? false) && !(old["requestBody"]?["required"]?.GetValue<bool>() ?? false))
        {
            found.Add($"{label} request body became required");
        }

        CompareContent($"{label} request", old["requestBody"]?["content"], current["requestBody"]?["content"], found, ContractDirection.Request);
        var newResponses = current["responses"] as JsonObject ?? [];
        foreach (var (code, response) in old["responses"] as JsonObject ?? [])
        {
            if (!newResponses.TryGetPropertyValue(code, out var now))
            {
                if (code.StartsWith('2'))
                {
                    found.Add($"{label} response {code} removed");
                }

                continue;
            }

            CompareContent($"{label} response {code}", response?["content"], now?["content"], found, ContractDirection.Response);
        }
    }

    private static void CompareContent(string label, JsonNode? old, JsonNode? current, List<string> found, ContractDirection direction)
    {
        foreach (var (media, body) in old as JsonObject ?? [])
        {
            if (current is JsonObject now && now.TryGetPropertyValue(media, out var next))
            {
                SchemaDiff.Compare($"{label} {media}", body?["schema"], next?["schema"], found, direction);
            }
            else
            {
                found.Add($"{label} media type {media} removed");
            }
        }
    }

    /// <summary>Parameters of the operation plus those declared on the path item (an operation-level one wins); path parameters are keyed by their position in the route.</summary>
    private static Dictionary<string, JsonObject> Parameters(JsonObject pathItem, JsonObject operation, string route)
    {
        var names = Regex.Matches(route, @"\{([^}]*)\}", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => m.Groups[1].Value).ToList();
        string Key(JsonObject p) =>
            p["$ref"]?.GetValue<string>() ?? (p["in"]?.GetValue<string>() == "path" && names.IndexOf(p["name"]?.GetValue<string>() ?? string.Empty) is var index and >= 0 ? $"path:#{index}" : $"{p["in"]}:{p["name"]}");
        return (pathItem["parameters"] as JsonArray ?? []).Concat(operation["parameters"] as JsonArray ?? []).OfType<JsonObject>()
            .GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
    }
}
