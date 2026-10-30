using System.Text.Json;
using System.Text.Json.Nodes;

namespace KirillMaster.QualityGates.Architecture.Contracts;

/// <summary>Which way a schema travels; enum and required changes are breaking in different directions.</summary>
public enum ContractDirection
{
    /// <summary>Unknown or shared (component schemas, message contracts): any change that can break a reader or a writer, except additive enum values and optional properties.</summary>
    Both,

    /// <summary>Sent by clients: removed enum values, newly required properties and narrowed nullability break.</summary>
    Request,

    /// <summary>Received by clients: added enum values, properties that stop being required, removed properties and widened nullability break.</summary>
    Response,
}

/// <summary>Breaking-change detection over JSON-schema-shaped nodes (OpenAPI schemas and message schemas).</summary>
internal static class SchemaDiff
{
    private static readonly string[] Combinators = ["allOf", "oneOf", "anyOf"];

    public static void Compare(string path, JsonNode? old, JsonNode? current, List<string> found, ContractDirection direction = ContractDirection.Both)
    {
        if (old is null || old.GetValueKind() == JsonValueKind.Null)
        {
            return;
        }

        if (current is not JsonObject now || old is not JsonObject before)
        {
            if (!JsonNode.DeepEquals(old, current))
            {
                found.Add($"{path} changed shape");
            }

            return;
        }

        if (before["$ref"] is not null || now["$ref"] is not null)
        {
            if (!JsonNode.DeepEquals(before["$ref"], now["$ref"]))
            {
                found.Add($"{path} now refers to {now["$ref"]?.ToJsonString() ?? "an inline schema"}");
            }

            return;
        }

        CompareScalars(path, before, now, found, direction);
        CompareProperties(path, before, now, found, direction);
        Compare(path + "[]", before["items"], now["items"], found, direction);
        Compare(path + "{}", before["additionalProperties"] as JsonObject, now["additionalProperties"], found, direction);
        foreach (var combinator in Combinators.Where(c => before[c] is JsonArray))
        {
            var oldItems = (JsonArray)before[combinator]!;
            var newItems = now[combinator] as JsonArray ?? [];
            for (var i = 0; i < oldItems.Count; i++)
            {
                if (!newItems.Any(n => Clean(oldItems[i], n, direction)))
                {
                    found.Add($"{path}.{combinator}[{i}] removed or changed incompatibly");
                }
            }
        }
    }

    private static bool Clean(JsonNode? old, JsonNode? current, ContractDirection direction)
    {
        var found = new List<string>();
        Compare("x", old, current, found, direction);
        return found.Count == 0;
    }

    private static void CompareScalars(string path, JsonObject before, JsonObject now, List<string> found, ContractDirection direction)
    {
        if (before["type"] is { } type && !JsonNode.DeepEquals(type, now["type"]))
        {
            found.Add($"{path} type {type.ToJsonString()} became {now["type"]?.ToJsonString() ?? "none"}");
        }

        if (before["format"] is { } format && !JsonNode.DeepEquals(format, now["format"]))
        {
            found.Add($"{path} format {format.ToJsonString()} became {now["format"]?.ToJsonString() ?? "none"}");
        }

        var wasNullable = before["nullable"]?.GetValueKind() == JsonValueKind.True;
        var isNullable = now["nullable"]?.GetValueKind() == JsonValueKind.True;
        if (wasNullable && !isNullable && direction != ContractDirection.Response)
        {
            found.Add($"{path} is no longer nullable");
        }
        else if (!wasNullable && isNullable && direction == ContractDirection.Response)
        {
            found.Add($"{path} became nullable");
        }

        var oldEnum = Strings(before["enum"]);
        var newEnum = Strings(now["enum"]);
        if (direction != ContractDirection.Response)
        {
            found.AddRange(oldEnum.Except(newEnum, StringComparer.Ordinal).Select(removed => $"{path} enum value {removed} removed"));
        }

        if (direction == ContractDirection.Response && oldEnum.Count > 0)
        {
            found.AddRange(newEnum.Except(oldEnum, StringComparer.Ordinal).Select(added => $"{path} enum value {added} added"));
        }
    }

    private static void CompareProperties(string path, JsonObject before, JsonObject now, List<string> found, ContractDirection direction)
    {
        var oldRequired = Strings(before["required"]);
        var newRequired = Strings(now["required"]);
        if (direction != ContractDirection.Response)
        {
            found.AddRange(newRequired.Except(oldRequired, StringComparer.Ordinal).Select(added => $"{path}.{added} is now required"));
        }

        if (direction == ContractDirection.Response)
        {
            found.AddRange(oldRequired.Except(newRequired, StringComparer.Ordinal).Select(dropped => $"{path}.{dropped} is no longer required"));
        }

        foreach (var (name, property) in before["properties"] as JsonObject ?? [])
        {
            if (now["properties"] is JsonObject properties && properties.ContainsKey(name))
            {
                Compare($"{path}.{name}", property, properties[name], found, direction);
            }
            else if (direction != ContractDirection.Request)
            {
                found.Add($"{path}.{name} removed");
            }
        }
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array ? [.. array.Where(i => i is not null).Select(i => i is JsonValue v && v.TryGetValue<string>(out var text) ? text : i!.ToJsonString())] : [];
}
