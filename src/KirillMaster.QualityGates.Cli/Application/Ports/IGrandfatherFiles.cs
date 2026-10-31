namespace KirillMaster.QualityGates.Cli.Application.Ports;

public interface IGrandfatherFiles
{
    IReadOnlyList<string> ExistingProjects(string repoRoot);

    void Write(string repoRoot, string project, string content);

    void Delete(string repoRoot, string project);
}
