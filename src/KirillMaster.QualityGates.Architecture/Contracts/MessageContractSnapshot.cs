using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Architecture.Contracts;

/// <summary>
/// Y8 helper: JSON-schema snapshot of message contracts (SNS, saga, bus messages) and a breaking-change diff. Properties follow System.Text.Json defaults with camelCase names,
/// <c>[JsonPropertyName]</c> and <c>[JsonIgnore]</c>; enums are their names. Call it from a test class:
/// <c>MessageContractSnapshot.Assert(MessageContractSnapshot.Discover(typeof(OrderPlaced).Assembly, "Messages"), "quality/messages.json")</c>.
/// </summary>
public static class MessageContractSnapshot
{
    private static readonly NullabilityInfoContext Nullability = new();

    /// <summary>Public non-abstract classes, records and structs of the assembly whose full name matches <paramref name="namePattern"/>.</summary>
    public static IReadOnlyList<Type> Discover(Assembly assembly, string namePattern)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var regex = new Regex(namePattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return [.. assembly.GetExportedTypes().Where(t => t is { IsAbstract: false, IsInterface: false, IsEnum: false, IsGenericTypeDefinition: false } && !typeof(Delegate).IsAssignableFrom(t) && regex.IsMatch(t.FullName ?? t.Name))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)];
    }

    /// <summary>Canonical document {"contracts": {"Full.Type.Name": schema}}.</summary>
    public static string Build(IEnumerable<Type> contracts)
    {
        var root = new JsonObject();
        foreach (var type in contracts.OrderBy(NameOf, StringComparer.Ordinal))
        {
            root[NameOf(type)] = SchemaOf(type, []);
        }

        return SnapshotStore.Normalize(new JsonObject { ["contracts"] = root }.ToJsonString());
    }

    /// <summary>Fails when the schemas of <paramref name="contracts"/> differ from the snapshot file (see <see cref="SnapshotStore"/> for the update switch).</summary>
    public static void Assert(IEnumerable<Type> contracts, string snapshotPath) => SnapshotStore.Assert(Build(contracts), snapshotPath, BreakingChanges);

    /// <summary>Removed contracts, removed or retyped properties, removed enum values and newly required properties.</summary>
    public static IReadOnlyList<string> BreakingChanges(string oldJson, string newJson)
    {
        var old = JsonNode.Parse(oldJson)?["contracts"] as JsonObject ?? [];
        var current = JsonNode.Parse(newJson)?["contracts"] as JsonObject ?? [];
        var found = new List<string>();
        foreach (var (name, schema) in old)
        {
            if (current.TryGetPropertyValue(name, out var now))
            {
                SchemaDiff.Compare(name, schema, now, found);
            }
            else
            {
                found.Add($"contract {name} removed");
            }
        }

        return found;
    }

    /// <summary>Stable contract name: the full name, with closed generics as <c>Ns.Envelope&lt;Ns.Payload&gt;</c> instead of the assembly-qualified runtime name.</summary>
    public static string NameOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
        return $"{Regex.Replace(definition, @"`\d+", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(1))}<{string.Join(',', type.GetGenericArguments().Select(NameOf))}>";
    }

    private static JsonObject SchemaOf(Type type, HashSet<Type> path)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            var inner = SchemaOf(underlying, path);
            inner["nullable"] = true;
            return inner;
        }

        if (Primitive(type) is { } primitive)
        {
            return primitive;
        }

        if (type.IsEnum)
        {
            return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. Enum.GetNames(type).Order(StringComparer.Ordinal).Select(n => (JsonNode)JsonValue.Create(n)!)]) };
        }

        if (DictionaryValue(type) is { } value)
        {
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = SchemaOf(value, path) };
        }

        if (ElementOf(type) is { } element)
        {
            return new JsonObject { ["type"] = "array", ["items"] = SchemaOf(element, path) };
        }

        if (type == typeof(object))
        {
            return [];
        }

        if (!path.Add(type))
        {
            return new JsonObject { ["$ref"] = "#/contracts/" + NameOf(type) };
        }

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            properties[name] = SchemaOf(property.PropertyType, path);
            if (IsRequired(property))
            {
                required.Add(name);
            }
        }

        path.Remove(type);
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static bool IsRequired(PropertyInfo property) =>
        property.GetCustomAttributes().Any(a => a.GetType().Name is "RequiredAttribute" or "RequiredMemberAttribute" or "JsonRequiredAttribute")
        || (property.PropertyType.IsValueType ? Nullable.GetUnderlyingType(property.PropertyType) is null : Nullability.Create(property).ReadState == NullabilityState.NotNull);

    private static Type? DictionaryValue(Type type) =>
        type.GetInterfaces().Prepend(type).FirstOrDefault(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))?.GetGenericArguments()[1];

    private static Type? ElementOf(Type type) =>
        type.IsArray ? type.GetElementType() : type.GetInterfaces().Prepend(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];

    private static JsonObject? Primitive(Type type)
    {
        if (type == typeof(byte[]))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "byte" };
        }

        if (type == typeof(Guid))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        }

        if (type == typeof(string) || type == typeof(char) || type == typeof(TimeSpan) || type == typeof(Uri) || type == typeof(DateOnly) || type == typeof(TimeOnly))
        {
            return new JsonObject { ["type"] = "string" };
        }

        if (type == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }

        if (type == typeof(long) || type == typeof(ulong))
        {
            return new JsonObject { ["type"] = "integer", ["format"] = "int64" };
        }

        if (type == typeof(int) || type == typeof(short) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(uint) || type == typeof(ushort))
        {
            return new JsonObject { ["type"] = "integer", ["format"] = "int32" };
        }

        return type == typeof(double) || type == typeof(float) || type == typeof(decimal) ? new JsonObject { ["type"] = "number" } : null;
    }
}
