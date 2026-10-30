using System.Globalization;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

public sealed record PublicType(string Path, string Name);

/// <summary>C30 (warn report): a public type declared in a file changed by the PR whose name appears in no other source file of the repo. Name based, so it can miss reflection use; it is a hint, never a failure.</summary>
public static class DeadPublicGate
{
    public static GateResult Evaluate(IReadOnlyCollection<PublicType> declared, IReadOnlyDictionary<string, IReadOnlySet<string>> identifiersByFile)
    {
        if (declared.Count == 0)
        {
            return new GateResult(GateId.DeadPublic, Verdict.NotApplicable, Message: "no public types declared in changed files");
        }

        var offenders = declared
            .Where(t => !identifiersByFile.Any(f => !string.Equals(f.Key, t.Path, StringComparison.Ordinal) && f.Value.Contains(t.Name)))
            .Select(t => $"{t.Path}: public {t.Name} is not referenced from any other file")
            .Order(StringComparer.Ordinal)
            .ToList();
        return offenders.Count == 0
            ? new GateResult(GateId.DeadPublic, Verdict.Pass, Actual: 0, Threshold: 0, Message: $"{declared.Count.ToString(CultureInfo.InvariantCulture)} public type(s) in changed files, all referenced")
            : new GateResult(GateId.DeadPublic, Verdict.Warn, Actual: offenders.Count, Threshold: 0, Offenders: offenders, Message: $"{offenders.Count.ToString(CultureInfo.InvariantCulture)} public type(s) without references (hint only)");
    }
}
