using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Application.Ports;

/// <summary>Outcome of one mutation run. A timed-out run never carries mutants: a partial report is not scored.</summary>
public sealed record MutationRun(bool TimedOut, IReadOnlyList<MutantResult> Mutants)
{
    public static MutationRun Completed(IReadOnlyList<MutantResult> mutants) => new(false, mutants);

    public static MutationRun BudgetExceeded { get; } = new(true, []);
}

/// <summary>The mutation report is missing or malformed: an input error, never a guessed score.</summary>
public sealed class MutationReportException(string message, Exception? inner = null) : Exception(message, inner);

public interface IMutationRunner
{
    /// <summary>Runs mutation testing for the diff against <paramref name="targetBranch"/>; cancellation means the budget ran out.</summary>
    Task<MutationRun> RunAsync(string repoPath, string targetBranch, CancellationToken cancellationToken = default);
}
