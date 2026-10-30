using KirillMaster.QualityGates.Cli.Domain.Grandfather;

namespace KirillMaster.QualityGates.Cli.Application.Ports;

public interface IBannedCallScanner
{
    /// <summary>Finds RS0030 call sites; uses the given SARIF files, or runs a scan build when none are given.</summary>
    IReadOnlyList<BannedCallSite> Scan(string repoRoot, IReadOnlyList<string> sarifPaths);
}
