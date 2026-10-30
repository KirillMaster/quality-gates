using KirillMaster.QualityGates.Cli.Domain.Gates;

namespace KirillMaster.QualityGates.Cli.Application.Ports;

public interface IFormatChecker
{
    /// <summary>Repo-relative, forward-slash paths of files that <c>dotnet format whitespace --folder --verify-no-changes</c> would change.</summary>
    Task<IReadOnlyList<string>> UnformattedFilesAsync(string repo, CancellationToken cancellationToken);
}

public interface IDuplicationScanner
{
    Task<IReadOnlyList<DuplicateBlock>> ScanAsync(string repo, CancellationToken cancellationToken);
}

public interface IDuplicationBaselineStore
{
    IReadOnlySet<string>? Load(string path);

    void Save(string path, IReadOnlyCollection<string> fingerprints);
}
