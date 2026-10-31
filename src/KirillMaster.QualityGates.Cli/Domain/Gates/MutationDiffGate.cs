using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

public static class MutationDiffGate
{
    public const double AdapterThreshold = 80;

    public static IReadOnlyList<GateResult> Evaluate(
        IReadOnlyList<MutantResult> mutants,
        Func<string, Layer> layerOf,
        GatesConfig config,
        Policy policy,
        bool timedOut = false)
    {
        if (policy == Policy.Skip)
        {
            return [new GateResult(GateId.MutationDiff, Verdict.Skip, ResultLayer.All, Message: "skip: mutation testing is not enforced for this branch pair")];
        }

        if (timedOut)
        {
            return [new GateResult(GateId.MutationDiff, Verdict.Timeout, ResultLayer.All,
                Message: $"mutation run exceeded the {config.Mutation.DiffBudgetMinutes} minute budget; the build is UNSTABLE")];
        }

        var core = EvaluateLayer(mutants, layerOf, Layer.Core, ResultLayer.Core, config.Mutation.CoreThreshold,
            failVerdict: policy == Policy.Blocking ? Verdict.Fail : Verdict.Warn);
        var adapter = EvaluateLayer(mutants, layerOf, Layer.Adapter, ResultLayer.Adapter, AdapterThreshold,
            failVerdict: Verdict.Warn);
        return [core, adapter];
    }

    private static GateResult EvaluateLayer(
        IReadOnlyList<MutantResult> mutants,
        Func<string, Layer> layerOf,
        Layer layer,
        ResultLayer resultLayer,
        double threshold,
        Verdict failVerdict)
    {
        var detected = 0;
        var undetected = new List<MutantResult>();
        foreach (var m in mutants)
        {
            if (layerOf(m.File) != layer)
            {
                continue;
            }

            if (m.IsDetected)
            {
                detected++;
            }
            else if (m.IsUndetected)
            {
                undetected.Add(m);
            }
        }

        var total = detected + undetected.Count;
        if (total == 0)
        {
            return new GateResult(GateId.MutationDiff, Verdict.NotApplicable, resultLayer);
        }

        var actual = detected * 100.0 / total;
        var passes = (decimal)detected * 100m >= (decimal)threshold * total;
        if (passes)
        {
            return new GateResult(GateId.MutationDiff, Verdict.Pass, resultLayer, actual, threshold);
        }

        undetected.Sort(CompareByLocation);
        var offenders = new List<string>(undetected.Count);
        foreach (var m in undetected)
        {
            offenders.Add($"{m.File}:{m.Line} {m.Mutator}");
        }

        return new GateResult(GateId.MutationDiff, failVerdict, resultLayer, actual, threshold, offenders);
    }

    private static int CompareByLocation(MutantResult a, MutantResult b)
    {
        var byFile = string.CompareOrdinal(a.File, b.File);
        return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
    }
}
