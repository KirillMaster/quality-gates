using KirillMaster.QualityGates.Cli.Application.Ports;

namespace KirillMaster.QualityGates.Cli.Adapters.Grandfather;

public sealed class GrandfatherFiles : IGrandfatherFiles
{
    public const string Suffix = ".GlobalSuppressions.cs";
    private const string Marker = "SuppressMessage(\"ApiDesign\", \"RS0030\"";

    public static string DirectoryOf(string repoRoot) => Path.Combine(repoRoot, "quality", "grandfathered");

    public IReadOnlyList<string> ExistingProjects(string repoRoot)
    {
        var directory = DirectoryOf(repoRoot);
        var projects = new List<string>();
        if (!Directory.Exists(directory))
        {
            return projects;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*" + Suffix))
        {
            var name = Path.GetFileName(file);
            projects.Add(name[..^Suffix.Length]);
        }

        projects.Sort(StringComparer.Ordinal);
        return projects;
    }

    public void Write(string repoRoot, string project, string content)
    {
        var directory = DirectoryOf(repoRoot);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, project + Suffix), content);
    }

    public void Delete(string repoRoot, string project)
    {
        var path = Path.Combine(DirectoryOf(repoRoot), project + Suffix);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Grandfathered RS0030 call-site count per project, read from the checked-in suppression files.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> CountSites(string repoRoot)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var project in new GrandfatherFiles().ExistingProjects(repoRoot))
        {
            var count = 0;
            foreach (var line in File.ReadLines(Path.Combine(DirectoryOf(repoRoot), project + Suffix)))
            {
                if (line.Contains(Marker, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            if (count > 0)
            {
                result[project] = new Dictionary<string, int> { ["RS0030"] = count };
            }
        }

        return result;
    }
}
