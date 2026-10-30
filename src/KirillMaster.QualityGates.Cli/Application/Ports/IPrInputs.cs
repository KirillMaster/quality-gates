using KirillMaster.QualityGates.Cli.Domain.Coverage;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Application.Ports;

public sealed record ProjectRef(string Name, Layer Layer);

public interface IPrFiles
{
    /// <summary>Expands file paths, directories and glob patterns (*, ?, **) to existing files.</summary>
    IReadOnlyList<string> Expand(IEnumerable<string> patterns);

    bool Exists(string path);
}

public interface IProjectLocator
{
    /// <summary>Finds the project owning a repo-relative file, or null when no project file is found above it.</summary>
    ProjectRef? Locate(string repoRoot, string relativeFile, IReadOnlyDictionary<string, Layer> overrides);
}

public interface ICoverageReader
{
    CoverageReport ReadMerged(IReadOnlyList<string> files, string repoRoot);

    /// <summary>Reads reports built in another checkout (the base worktree): paths are made repo-relative against <paramref name="reportRoot" />, or against a root detected from the report when it is null.</summary>
    CoverageReport ReadMergedFromOtherRoot(IReadOnlyList<string> files, string repoRoot, string? reportRoot) => ReadMerged(files, repoRoot);
}

public interface ISarifSource
{
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Aggregate(IReadOnlyList<string> files);

    /// <summary>Unsuppressed warning-or-higher results with their locations, de-duplicated across target frameworks.</summary>
    IReadOnlyList<Finding> Read(IReadOnlyList<string> files);
}

public interface IReportStore
{
    void Write(string outDirectory, RunReport report);

    void WriteDebt(string outDirectory, DebtReport report);
}

public interface IMutationVerdictStore
{
    void Write(string outDirectory, RunReport report);
}
