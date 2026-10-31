using System.Text;
using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

/// <summary>Glob match on repo-relative paths with forward slashes: <c>*</c> and <c>?</c> stay within a segment, <c>**</c> crosses segments. A pattern without a slash matches the file name at any depth, as in .gitattributes.</summary>
public static class PathGlob
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    public static bool IsMatch(string pattern, string path)
    {
        var normalized = path.Replace('\\', '/');
        var glob = pattern.Replace('\\', '/').TrimStart('/');
        if (!glob.Contains('/', StringComparison.Ordinal))
        {
            glob = "**/" + glob;
        }

        return new Regex(ToRegex(glob), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout).IsMatch(normalized);
    }

    public static bool IsMatchAny(IEnumerable<string> patterns, string path) => patterns.Any(p => IsMatch(p, path));

    private static string ToRegex(string pattern)
    {
        var text = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
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
