using System.Text.Json.Nodes;

namespace KirillMaster.QualityGates.Architecture.Contracts;

/// <summary>Breaking-change detection over JSON-schema-shaped nodes (OpenAPI schemas and message schemas). Direction-agnostic: anything that can break a reader or a writer counts.</summary>
internal static class SchemaDiff
{
    private static readonly string[] Combinators = ["allOf", "oneOf", "anyOf"];

    public static void Compare(string path, JsonNode? old, JsonNode? current, List<string> found)
    {
        if (old is null || old.GetValueKind() == System.Text.Json.JsonValueKind.Null)
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

        if (before["type"] is { } type && !JsonNode.DeepEquals(type, now["type"]))
        {
            found.Add($"{path} type {type.ToJsonString()} became {now["type"]?.ToJsonString() ?? "none"}");
        }

        foreach (var removed in Strings(before["enum"]).Except(Strings(now["enum"]), StringComparer.Ordinal))
        {
            found.Add($"{path} enum value {removed} removed");
        }

        foreach (var added in Strings(now["required"]).Except(Strings(before["required"]), StringComparer.Ordinal))
        {
            found.Add($"{path}.{added} is now required");
        }

        foreach (var (name, property) in before["properties"] as JsonObject ?? [])
        {
            if (now["properties"] is JsonObject properties && properties.ContainsKey(name))
            {
                Compare($"{path}.{name}", property, properties[name], found);
            }
            else
            {
                found.Add($"{path}.{name} removed");
            }
        }

        Compare(path + "[]", before["items"], now["items"], found);
        Compare(path + "{}", before["additionalProperties"] as JsonObject, now["additionalProperties"], found);
        foreach (var combinator in Combinators.Where(c => before[c] is JsonArray))
        {
            var oldItems = (JsonArray)before[combinator]!;
            var newItems = now[combinator] as JsonArray ?? [];
            if (newItems.Count < oldItems.Count)
            {
                found.Add($"{path}.{combinator} lost alternatives");
                continue;
            }

            for (var i = 0; i < oldItems.Count; i++)
            {
                Compare($"{path}.{combinator}[{i}]", oldItems[i], newItems[i], found);
            }
        }
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node is JsonArray array ? array.Where(i => i is not null).Select(i => i is JsonValue v && v.TryGetValue<string>(out var text) ? text : i!.ToJsonString()) : [];
}
