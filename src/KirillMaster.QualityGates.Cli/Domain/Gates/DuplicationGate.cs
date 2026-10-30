using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

/// <summary>One duplicated block; the fingerprint hashes the whitespace-normalised fragment so line shifts do not create "new" blocks.</summary>
public sealed record DuplicateBlock(string Fingerprint, string FirstFile, int FirstStart, string SecondFile, int SecondStart, int Lines);

/// <summary>Ratchet on duplicated blocks: blocks absent from the baseline fail, existing debt passes; with no baseline yet the gate passes and a seed baseline is written.</summary>
public static class DuplicationGate
{
    public static GateResult Evaluate(IReadOnlyCollection<DuplicateBlock> current, IReadOnlySet<string>? baseline)
    {
        if (baseline is null)
        {
            return new GateResult(GateId.Duplication, Verdict.Pass, Message: $"no duplication baseline; {current.Count} existing block(s) recorded");
        }

        var added = new List<string>();
        foreach (var block in current)
        {
            if (!baseline.Contains(block.Fingerprint))
            {
                added.Add($"{block.FirstFile}:{block.FirstStart} <-> {block.SecondFile}:{block.SecondStart} ({block.Lines} lines)");
            }
        }

        added.Sort(StringComparer.Ordinal);
        return added.Count == 0
            ? new GateResult(GateId.Duplication, Verdict.Pass, Message: $"no new duplicated blocks ({current.Count} existing)")
            : new GateResult(GateId.Duplication, Verdict.Fail, Actual: added.Count, Threshold: 0, Offenders: added, Message: $"{added.Count} new duplicated block(s)");
    }
}
