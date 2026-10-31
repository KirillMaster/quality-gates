using System.Globalization;
using System.Text.RegularExpressions;

namespace KirillMaster.QualityGates.Cli.Domain.Diff;

public static class DiffParser
{
    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static ChangedLines Parse(string diff)
    {
        var result = new ChangedLines();
        string? path = null;
        var remainingOld = 0;
        var remainingNew = 0;
        var nextNewLine = 0;

        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (remainingOld > 0 || remainingNew > 0)
            {
                if (line.StartsWith('+'))
                {
                    if (path is not null)
                    {
                        result.Add(path, nextNewLine);
                    }

                    nextNewLine++;
                    remainingNew--;
                    continue;
                }

                if (line.StartsWith('-'))
                {
                    remainingOld--;
                    continue;
                }

                if (line.StartsWith('\\'))
                {
                    continue;
                }
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                path = null;
                remainingOld = 0;
                remainingNew = 0;
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                path = NewPath(line[4..]);
            }
            else
            {
                var m = HunkHeader.Match(line);
                if (!m.Success)
                {
                    continue;
                }

                remainingOld = LineCount(m.Groups[2]);
                remainingNew = LineCount(m.Groups[4]);
                nextNewLine = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            }
        }

        return result;
    }

    private static int LineCount(Group group) =>
        group.Success ? int.Parse(group.Value, CultureInfo.InvariantCulture) : 1;

    private static string? NewPath(string raw)
    {
        var p = raw.Split('\t')[0].Trim().Trim('"');
        if (p == "/dev/null")
        {
            return null;
        }

        if (p.StartsWith("b/", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        return p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? p : null;
    }
}
