using KirillMaster.QualityGates.Cli.Domain.Gates;

namespace KirillMaster.QualityGates.Cli.Application.Ports;

/// <summary>Which side of a pull request a file is read from: the merge base, HEAD, or the tip of the target branch.</summary>
public enum PrRevision { Base, Head, Target }

public interface IPrChangeSource
{
    /// <summary>Files changed in <c>{target}...HEAD</c> with added and deleted line counts, renames detected.</summary>
    Task<IReadOnlyList<FileChange>> ChangesAsync(string repoPath, string target, CancellationToken cancellationToken = default);

    /// <summary>Returns the file content at the revision, or null when the file does not exist there; any other git failure throws.</summary>
    Task<string?> ReadAsync(string repoPath, string target, PrRevision revision, string path, CancellationToken cancellationToken = default);

    /// <summary>Reads many files at one revision with a single git process; a file that does not exist there maps to null.</summary>
    Task<IReadOnlyDictionary<string, string?>> ReadManyAsync(string repoPath, string target, PrRevision revision, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Returns every file path at the merge base of the target and HEAD.</summary>
    Task<IReadOnlyList<string>> BaseFilesAsync(string repoPath, string target, CancellationToken cancellationToken = default);

    /// <summary>Returns the zero-context unified diff from <paramref name="commit" /> to HEAD (two-dot, no merge-base lookup).</summary>
    Task<string> DiffFromAsync(string repoPath, string commit, CancellationToken cancellationToken = default);

    /// <summary>Returns every file path at HEAD.</summary>
    Task<IReadOnlyList<string>> HeadFilesAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>Returns the changed file paths of each of the last <paramref name="count" /> non-merge commits on the target branch.</summary>
    Task<IReadOnlyList<IReadOnlyList<string>>> HistoryAsync(string repoPath, string target, int count, CancellationToken cancellationToken = default);
}
