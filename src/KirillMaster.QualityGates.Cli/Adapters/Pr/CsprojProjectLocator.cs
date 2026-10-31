using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Adapters.Pr;

public sealed class CsprojProjectLocator : IProjectLocator
{
    private readonly Dictionary<string, string?> _projectByDir = new(StringComparer.OrdinalIgnoreCase);

    public ProjectRef? Locate(string repoRoot, string relativeFile, IReadOnlyDictionary<string, Layer> overrides)
    {
        var root = Path.GetFullPath(repoRoot);
        var dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, relativeFile)));
        var csproj = dir is null ? null : FindProject(dir, root);
        if (csproj is null)
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(csproj);
        var relDir = Path.GetRelativePath(root, Path.GetDirectoryName(csproj)!).Replace(Path.DirectorySeparatorChar, '/');
        return new ProjectRef(name, LayerClassifier.Classify(name, relDir, overrides).Layer);
    }

    private string? FindProject(string startDir, string root)
    {
        if (_projectByDir.TryGetValue(startDir, out var cached))
        {
            return cached;
        }

        string? result = null;
        if (Directory.Exists(startDir))
        {
            result = Directory.EnumerateFiles(startDir, "*.csproj").Order(StringComparer.Ordinal).FirstOrDefault();
        }

        if (result is null && !string.Equals(startDir, root, StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(startDir);
            if (parent is not null)
            {
                result = FindProject(parent, root);
            }
        }

        _projectByDir[startDir] = result;
        return result;
    }
}
