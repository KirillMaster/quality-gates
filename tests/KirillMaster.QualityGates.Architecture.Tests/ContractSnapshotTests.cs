using System.Text.Json.Nodes;
using KirillMaster.QualityGates.Architecture.Contracts;

namespace KirillMaster.QualityGates.Architecture.Tests;

public sealed class ContractSnapshotTests
{
    private const string OpenApi = @"{
  ""openapi"": ""3.0.1"", ""info"": { ""title"": ""Shop"", ""version"": ""1.0"" }, ""servers"": [{ ""url"": ""http://x"" }],
  ""paths"": {
    ""/orders/{id}"": {
      ""get"": {
        ""parameters"": [ { ""name"": ""id"", ""in"": ""path"", ""required"": true, ""schema"": { ""type"": ""string"" } } ],
        ""responses"": { ""200"": { ""content"": { ""application/json"": { ""schema"": { ""$ref"": ""#/components/schemas/Order"" } } } }, ""404"": { } }
      },
      ""delete"": { ""responses"": { ""204"": { } } }
    },
    ""/orders"": { ""post"": { ""requestBody"": { ""content"": { ""application/json"": { ""schema"": { ""$ref"": ""#/components/schemas/Order"" } } } }, ""responses"": { ""201"": { } } } }
  },
  ""components"": { ""schemas"": { ""Order"": { ""type"": ""object"", ""required"": [""id""], ""properties"": { ""id"": { ""type"": ""string"" }, ""status"": { ""type"": ""string"", ""enum"": [""New"", ""Paid""] }, ""note"": { ""type"": ""string"" } } } } }
}";

    private static string Edit(Action<JsonNode> change)
    {
        var node = JsonNode.Parse(OpenApi)!;
        change(node);
        return node.ToJsonString();
    }

    [Fact]
    public void Additive_openapi_changes_are_not_breaking()
    {
        var current = Edit(n =>
        {
            n["paths"]!["/orders"]!["get"] = new JsonObject { ["responses"] = new JsonObject { ["200"] = new JsonObject() } };
            n["paths"]!["/orders/{id}"]!["get"]!["parameters"]!.AsArray().Add(new JsonObject { ["name"] = "expand", ["in"] = "query", ["schema"] = new JsonObject { ["type"] = "string" } });
            n["components"]!["schemas"]!["Order"]!["properties"]!["extra"] = new JsonObject { ["type"] = "string" };
            n["components"]!["schemas"]!["Order"]!["properties"]!["status"]!["enum"]!.AsArray().Add("Shipped");
        });

        Assert.Empty(OpenApiSnapshot.BreakingChanges(OpenApi, current));
    }

    [Fact]
    public void Removed_and_tightened_openapi_parts_are_breaking()
    {
        var current = Edit(n =>
        {
            n["paths"]!.AsObject().Remove("/orders");
            n["paths"]!["/orders/{id}"]!.AsObject().Remove("delete");
            n["paths"]!["/orders/{id}"]!["get"]!["parameters"]!.AsArray().Add(new JsonObject { ["name"] = "tenant", ["in"] = "header", ["required"] = true });
            n["paths"]!["/orders/{id}"]!["get"]!["responses"]!.AsObject().Remove("200");
            var order = n["components"]!["schemas"]!["Order"]!.AsObject();
            order["properties"]!.AsObject().Remove("note");
            order["properties"]!["status"]!["enum"]!.AsArray().RemoveAt(0);
            order["required"]!.AsArray().Add("status");
        });

        var found = OpenApiSnapshot.BreakingChanges(OpenApi, current);

        Assert.Contains("path /orders removed", found);
        Assert.Contains("operation DELETE /orders/{id} removed", found);
        Assert.Contains("GET /orders/{id} gained required parameter header:tenant", found);
        Assert.Contains("GET /orders/{id} response 200 removed", found);
        Assert.Contains("schema Order.note removed", found);
        Assert.Contains("schema Order.status enum value New removed", found);
        Assert.Contains("schema Order.status is now required", found);
    }

    [Fact]
    public void Snapshot_ignores_key_order_and_volatile_fields_and_fails_on_missing_or_changed_files()
    {
        var file = Path.Combine(Path.GetTempPath(), "qg-snap", Guid.NewGuid().ToString("N"), "openapi.json");

        var missing = Assert.Throws<InvalidOperationException>(() => OpenApiSnapshot.Assert(OpenApi, file));
        Assert.Contains("missing", missing.Message, StringComparison.Ordinal);

        SnapshotStore.Assert(OpenApiSnapshot.Normalize(OpenApi), file, OpenApiSnapshot.BreakingChanges, update: true);
        var reordered = Edit(n =>
        {
            n["info"]!["version"] = "9.9";
            n["servers"] = new JsonArray();
        });
        OpenApiSnapshot.Assert(reordered, file);

        var changed = Edit(n => n["paths"]!.AsObject().Remove("/orders"));
        var ex = Assert.Throws<InvalidOperationException>(() => OpenApiSnapshot.Assert(changed, file));
        Assert.Contains("path /orders removed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_with_breaking_changes_needs_the_explicit_allowance()
    {
        var file = Path.Combine(Path.GetTempPath(), "qg-snap", Guid.NewGuid().ToString("N"), "openapi.json");
        SnapshotStore.Assert(OpenApiSnapshot.Normalize(OpenApi), file, OpenApiSnapshot.BreakingChanges, update: true);
        var breaking = OpenApiSnapshot.Normalize(Edit(n => n["paths"]!.AsObject().Remove("/orders")));

        Assert.Throws<InvalidOperationException>(() => SnapshotStore.Assert(breaking, file, OpenApiSnapshot.BreakingChanges, update: true, allowBreaking: false));
        SnapshotStore.Assert(breaking, file, OpenApiSnapshot.BreakingChanges, update: true, allowBreaking: true);
        OpenApiSnapshot.Assert(breaking, file, dropVolatile: false);
    }

    [Fact]
    public void Message_schema_lists_properties_required_and_enums()
    {
        var schema = JsonNode.Parse(MessageContractSnapshot.Build([typeof(OrderPlaced)]))!["contracts"]![typeof(OrderPlaced).FullName!]!;

        Assert.Equal(["customer", "id", "lines", "note", "status", "total"], schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["customer", "id", "lines", "status", "total"], schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal("uuid", (string?)schema["properties"]!["id"]!["format"]);
        Assert.Equal("array", (string?)schema["properties"]!["lines"]!["type"]);
        Assert.Equal(["Cancelled", "Placed"], schema["properties"]!["status"]!["enum"]!.AsArray().Select(e => e!.GetValue<string>()));
    }

    [Fact]
    public void Message_contract_breaking_changes_are_detected()
    {
        var old = MessageContractSnapshot.Build([typeof(OrderPlaced), typeof(Line)]);
        var edited = JsonNode.Parse(old)!;
        var contract = edited["contracts"]![typeof(OrderPlaced).FullName!]!;
        contract["properties"]!.AsObject().Remove("note");
        contract["properties"]!["total"]!["type"] = "string";
        edited["contracts"]!.AsObject().Remove(typeof(Line).FullName!);

        var found = MessageContractSnapshot.BreakingChanges(old, edited.ToJsonString());

        Assert.Contains($"contract {typeof(Line).FullName} removed", found);
        Assert.Contains($"{typeof(OrderPlaced).FullName}.total type \"number\" became \"string\"", found);
        Assert.Contains($"{typeof(OrderPlaced).FullName}.note removed", found);
        Assert.Empty(MessageContractSnapshot.BreakingChanges(old, old));
    }

    [Fact]
    public void Message_contracts_are_discovered_by_name()
    {
        var assembly = FixtureCompiler.Compile("MessagesFixture", "namespace Shop.Messages { public record Placed(int Id); public interface IX { } } namespace Shop.Other { public record Skip(int Id); }");

        Assert.Equal(["Shop.Messages.Placed"], MessageContractSnapshot.Discover(assembly, @"\.Messages\.").Select(t => t.FullName));
    }

    public enum Status
    {
        Placed,
        Cancelled,
    }

    public sealed record Line(string Sku, int Quantity);

    public sealed record OrderPlaced(Guid Id, string Customer, string? Note, decimal Total, Status Status, List<Line> Lines);
}
