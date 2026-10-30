using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

/// <summary>Files that fail <c>dotnet format whitespace --verify-no-changes</c> anywhere in the repo: on a PR they block, on a branch build they are only reported.</summary>
public static class FormatGate
{
    public static GateResult Evaluate(IReadOnlyCollection<string> unformatted, bool branchBuild)
    {
        if (unformatted.Count == 0)
        {
            return new GateResult(GateId.Format, Verdict.Pass, Message: "no files need whitespace formatting");
        }

        var offenders = unformatted.Order(StringComparer.Ordinal).ToList();
        return new GateResult(
            GateId.Format,
            branchBuild ? Verdict.Warn : Verdict.Fail,
            Actual: offenders.Count,
            Threshold: 0,
            Offenders: offenders,
            Message: $"{offenders.Count} file(s) need whitespace formatting; run `dotnet format whitespace --folder .`");
    }
}
