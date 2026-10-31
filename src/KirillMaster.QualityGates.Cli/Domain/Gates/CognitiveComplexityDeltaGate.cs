using System.Globalization;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

public sealed record MethodComplexityChange(string Path, IReadOnlyDictionary<string, int> Base, IReadOnlyDictionary<string, int> Head);

public sealed record ComplexityDeltaOptions(int NewMethodLimit = 15);

/// <summary>P5 / C18: the cognitive complexity of a method must not grow against the merge base. A method added by the PR may not exceed the new-method limit. Methods that vanished or got simpler never fail.</summary>
public static class CognitiveComplexityDeltaGate
{
    public static GateResult Evaluate(IReadOnlyCollection<MethodComplexityChange> changes, ComplexityDeltaOptions options)
    {
        if (changes.Count == 0)
        {
            return new GateResult(GateId.CognitiveComplexityDelta, Verdict.NotApplicable, Message: "no C# files changed");
        }

        var offenders = new List<string>();
        var measured = 0;
        foreach (var change in changes.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            foreach (var (method, head) in change.Head.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                measured++;
                if (change.Base.TryGetValue(method, out var before))
                {
                    if (head > before)
                    {
                        offenders.Add($"{change.Path}: {method} {Text(before)} -> {Text(head)}");
                    }
                }
                else if (head > options.NewMethodLimit)
                {
                    offenders.Add($"{change.Path}: {method} is new with complexity {Text(head)} (limit {Text(options.NewMethodLimit)})");
                }
            }
        }

        return offenders.Count == 0
            ? new GateResult(GateId.CognitiveComplexityDelta, Verdict.Pass, Actual: 0, Threshold: 0, Message: $"{Text(measured)} method(s) in changed files, none grew in cognitive complexity")
            : new GateResult(GateId.CognitiveComplexityDelta, Verdict.Fail, Actual: offenders.Count, Threshold: 0, Offenders: offenders, Message: $"{Text(offenders.Count)} method(s) grew in cognitive complexity; extract or simplify");
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
