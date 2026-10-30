using System.Text.Json;
using System.Text.Json.Nodes;

namespace KirillMaster.QualityGates.Architecture.Contracts;

/// <summary>
/// Committed snapshot files for generated contracts (OpenAPI, message schemas). A test compares the generated text with the file: a missing or different file fails, with the breaking changes listed.
/// Set <c>QG_UPDATE_SNAPSHOTS=1</c> to write the file (ignored when <c>CI</c> is set: CI fails instead); an update that contains breaking changes also needs <c>QG_ALLOW_BREAKING_CHANGES=1</c>.
/// </summary>
public static class SnapshotStore
{
    public const string UpdateVariable = "QG_UPDATE_SNAPSHOTS";

    public const string AllowBreakingVariable = "QG_ALLOW_BREAKING_CHANGES";

    internal static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Compares <paramref name="actual"/> (already normalised) with the file at <paramref name="path"/>; <paramref name="breaking"/> lists breaking changes from the old to the new text. <paramref name="update"/> and <paramref name="allowBreaking"/> override the environment switches.</summary>
    public static void Assert(string actual, string path, Func<string, string, IReadOnlyList<string>> breaking, bool? update = null, bool? allowBreaking = null)
    {
        ArgumentNullException.ThrowIfNull(breaking);
        path = Resolve(path);
        var exists = File.Exists(path);
        var expected = exists ? File.ReadAllText(path) : null;
        if (expected is not null && Same(expected, actual))
        {
            return;
        }

        var changes = expected is null ? [] : breaking(expected, actual);
        var ci = IsSet("CI");
        if ((update ?? (IsSet(UpdateVariable) && !ci)) && (changes.Count == 0 || (allowBreaking ?? IsSet(AllowBreakingVariable))))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, actual);
            return;
        }

        var reason = expected is null ? "Snapshot " + path + " is missing" : "Snapshot " + path + " differs from the generated contract";
        var detail = changes.Count > 0 ? " Breaking changes (also set " + AllowBreakingVariable + "=1 to accept): " + string.Join("; ", changes) + "." : string.Empty;
        throw new InvalidOperationException($"{reason}. Review the change and run with {UpdateVariable}=1 to update it{(ci && IsSet(UpdateVariable) ? " (ignored on CI, run it locally)" : string.Empty)}.{detail}");
    }

    /// <summary>Relative snapshot paths resolve against the repository root (<see cref="RepositoryRules.FindRepositoryRoot"/>: <c>QG_REPO_ROOT</c>, else the nearest folder with .git or a solution file above the test binaries), never against the working directory.</summary>
    public static string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(RepositoryRules.FindRepositoryRoot(), path));

    /// <summary>Canonical JSON: object keys sorted ordinally, indented, LF line endings, with <paramref name="drop"/> JSON paths ("info.version") removed.</summary>
    public static string Normalize(string json, params string[] drop)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) ?? throw new InvalidOperationException("Contract document is empty.");
        foreach (var path in drop)
        {
            Remove(root, path.Split('.'));
        }

        return Sort(root)!.ToJsonString(Pretty).ReplaceLineEndings("\n") + "\n";
    }

    private static bool Same(string a, string b) => string.Equals(a.ReplaceLineEndings("\n").TrimEnd(), b.ReplaceLineEndings("\n").TrimEnd(), StringComparison.Ordinal);

    private static bool IsSet(string name) => Environment.GetEnvironmentVariable(name) is "1" or "true" or "TRUE" or "True";

    private static void Remove(JsonNode? node, ReadOnlySpan<string> path)
    {
        if (node is not JsonObject obj || path.IsEmpty)
        {
            return;
        }

        if (path.Length == 1)
        {
            obj.Remove(path[0]);
        }
        else
        {
            Remove(obj[path[0]], path[1..]);
        }
    }

    private static JsonNode? Sort(JsonNode? node) =>
        node switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
            JsonArray array => new JsonArray([.. array.Select(i => Sort(i?.DeepClone()))]),
            _ => node?.DeepClone(),
        };
}
