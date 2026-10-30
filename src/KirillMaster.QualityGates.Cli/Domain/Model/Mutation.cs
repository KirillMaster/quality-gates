namespace KirillMaster.QualityGates.Cli.Domain.Model;

public enum MutantStatus { Killed, Timeout, Survived, NoCoverage, Ignored, CompileError, RuntimeError, Other }

/// <summary>One mutant of a Stryker report: repo-relative file, 1-based start line, mutator name and outcome.</summary>
public sealed record MutantResult(string File, int Line, string Mutator, MutantStatus Status)
{
    public bool IsDetected => Status is MutantStatus.Killed or MutantStatus.Timeout;

    public bool IsUndetected => Status is MutantStatus.Survived or MutantStatus.NoCoverage;
}
