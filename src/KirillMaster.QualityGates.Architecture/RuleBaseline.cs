using System.Text.Json;

namespace KirillMaster.QualityGates.Architecture;

/// <summary>One architecture finding. <see cref="Key"/> identifies it in a baseline, <see cref="Measure"/> is the size that may only shrink (edge count, member count, fan-out).</summary>
public sealed record RuleViolation(string Rule, string Key, int Measure, string Message);

/// <summary>Known violations per rule ({"rules": {"FeatureIsolation": {"A->B": 3}}}); a violation is tolerated while its measure does not exceed the baseline measure.</summary>
public sealed class RuleBaseline
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, WriteIndented = true };

    public Dictionary<string, Dictionary<string, int>> Rules
    {
        get;
        set => field = value ?? [];
    } = [];

    public static RuleBaseline FromJson(string json) =>
        JsonSerializer.Deserialize<RuleBaseline>(json, JsonOptions)
        ?? throw new InvalidOperationException("Rule baseline is empty.");

    /// <summary>Missing file means no violation is tolerated.</summary>
    public static RuleBaseline FromFile(string path) => File.Exists(path) ? FromJson(File.ReadAllText(path)) : new RuleBaseline();

    public static RuleBaseline From(IEnumerable<RuleViolation> violations) =>
        new()
        {
            Rules = violations.GroupBy(v => v.Rule, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Measure, StringComparer.Ordinal), StringComparer.Ordinal),
        };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public bool Tolerates(RuleViolation violation) =>
        Rules.TryGetValue(violation.Rule, out var keys) && keys.TryGetValue(violation.Key, out var measure) && violation.Measure <= measure;

    /// <summary>Renames keys after types or modules moved (exact key match, measure preserved; keys that collapse keep the larger measure). Unmapped keys stay.</summary>
    public RuleBaseline Remap(IReadOnlyDictionary<string, string> oldToNew)
    {
        var remapped = new RuleBaseline();
        foreach (var (rule, keys) in Rules.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var target = remapped.Rules[rule] = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (key, measure) in keys.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var renamed = oldToNew.TryGetValue(key, out var value) ? value : key;
                target[renamed] = target.TryGetValue(renamed, out var existing) ? Math.Max(existing, measure) : measure;
            }
        }

        return remapped;
    }

    public IReadOnlyList<RuleViolation> New(IEnumerable<RuleViolation> violations) => [.. violations.Where(v => !Tolerates(v))];

    /// <summary>Baseline entries that no longer occur or have shrunk; lower them so the baseline only ever shrinks.</summary>
    public IReadOnlyList<string> Stale(IEnumerable<RuleViolation> violations, IEnumerable<string>? rules = null)
    {
        var scope = rules?.ToHashSet(StringComparer.Ordinal);
        var current = violations.ToLookup(v => (v.Rule, v.Key)).ToDictionary(g => g.Key, g => g.Max(v => v.Measure));
        return [.. Rules.SelectMany(r => r.Value.Select(k => (Rule: r.Key, k.Key, Measure: k.Value)))
            .Where(e => scope is null || scope.Contains(e.Rule))
            .Where(e => !current.TryGetValue((e.Rule, e.Key), out var measure) || measure < e.Measure)
            .Select(e => $"{e.Rule}: {e.Key}").Order(StringComparer.Ordinal)];
    }
}
