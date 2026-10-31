using KirillMaster.QualityGates.Cli.Application.Ports;

namespace KirillMaster.QualityGates.Cli.Adapters.Pr;

public sealed class FileArtifactWriter : IArtifactWriter
{
    public void Write(string directory, string fileName, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    public string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
}
