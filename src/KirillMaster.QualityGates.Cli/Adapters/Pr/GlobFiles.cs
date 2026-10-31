using System.Text;
using System.Text.RegularExpressions;
using KirillMaster.QualityGates.Cli.Application.Ports;

namespace KirillMaster.QualityGates.Cli.Adapters.Pr;

public sealed class GlobFiles : IPrFiles
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    public bool Exists(string path) => File.Exists(path);

    public IReadOnlyList<string> Expand(IEnumerable<string> patterns)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            foreach (var file in ExpandOne(pattern))
            {
                found.Add(file);
            }
        }

        return [.. found];
    }

    private static IEnumerable<string> ExpandOne(string pattern)
    {
        var normalized = pattern.Replace(Path.DirectorySeparatorChar, '/');
        if (File.Exists(normalized))
        {
            return [Path.GetFullPath(normalized)];
        }

        if (Directory.Exists(normalized))
        {
            return Directory.EnumerateFiles(normalized, "*", SearchOption.AllDirectories).Select(Path.GetFullPath);
        }

        var wildcard = normalized.IndexOfAny(['*', '?']);
        if (wildcard < 0)
        {
            return [];
        }

        var split = normalized.LastIndexOf('/', wildcard);
        var baseDir = split < 0 ? "." : normalized[..split];
        if (split < 0)
        {
            normalized = "./" + normalized;
        }

        if (!Directory.Exists(baseDir))
        {
            return [];
        }

        var regex = new Regex(ToRegex(normalized), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        var matches = new List<string>();
        foreach (var file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
        {
            if (regex.IsMatch(file.Replace(Path.DirectorySeparatorChar, '/')))
            {
                matches.Add(Path.GetFullPath(file));
            }
        }

        return matches;
    }

    private static string ToRegex(string pattern)
    {
        var full = pattern;
        var text = new StringBuilder("^");
        for (var i = 0; i < full.Length; i++)
        {
            var c = full[i];
            if (c == '*' && i + 1 < full.Length && full[i + 1] == '*')
            {
                var slash = i + 2 < full.Length && full[i + 2] == '/';
                text.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*')
            {
                text.Append("[^/]*");
            }
            else if (c == '?')
            {
                text.Append("[^/]");
            }
            else
            {
                text.Append(Regex.Escape(c.ToString()));
            }
        }

        return text.Append('$').ToString();
    }
}
