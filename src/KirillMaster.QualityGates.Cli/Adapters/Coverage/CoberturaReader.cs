using System.Globalization;
using System.Xml.Linq;
using KirillMaster.QualityGates.Cli.Domain.Coverage;

namespace KirillMaster.QualityGates.Cli.Adapters.Coverage;

public static class CoberturaReader
{
    public static CoverageReport ReadMerged(IEnumerable<string> reportFiles, string repoRoot) =>
        CoverageReport.Merge(reportFiles.Select(f => Read(f, repoRoot)));

    /// <summary>Reads one report. For a report built in another checkout pass <paramref name="reportRoot" /> (that checkout's root) or set <paramref name="detectRoot" /> to derive it from the paths of the report.</summary>
    public static CoverageReport Read(string reportFile, string repoRoot, string? reportRoot = null, bool detectRoot = false)
    {
        var doc = XDocument.Load(reportFile);
        var sources = doc.Descendants("source").Select(s => Slashes(s.Value.Trim())).Where(s => s.Length > 0).ToList();
        var root = Slashes(repoRoot).TrimEnd('/');
        var anchor = !string.IsNullOrWhiteSpace(reportRoot) ? Slashes(reportRoot).TrimEnd('/') : detectRoot ? DetectRoot(doc, sources, root) ?? root : root;
        var report = new CoverageReport();

        foreach (var cls in doc.Descendants("class"))
        {
            var filename = (string?)cls.Attribute("filename");
            if (string.IsNullOrWhiteSpace(filename))
            {
                continue;
            }

            var path = Normalize(filename, root, anchor, sources);
            report.AddFile(path);
            foreach (var line in cls.Elements("lines").Elements("line"))
            {
                if (int.TryParse((string?)line.Attribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                    int.TryParse((string?)line.Attribute("hits"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hits))
                {
                    report.Record(path, number, hits);
                }
            }
        }

        return report;
    }

    // The root of the checkout a base report was built in (it may be nested inside this checkout, so a path under the HEAD root is only matched below its first folder): the prefix that turns report paths into paths existing in this repo (longest matching suffix, most votes);
    // without any surviving file, the common directory of the absolute <source> entries.
    private static string? DetectRoot(XDocument doc, List<string> sources, string repoRoot)
    {
        var absoluteSources = sources.Where(IsAbsolute).Select(s => s.TrimEnd('/')).ToList();
        var paths = new List<string>();
        foreach (var filename in doc.Descendants("class").Select(c => (string?)c.Attribute("filename")).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Slashes(f!)).Distinct().Take(300))
        {
            if (IsAbsolute(filename))
            {
                paths.Add(filename);
            }
            else
            {
                paths.AddRange(absoluteSources.Select(s => s + "/" + (filename.StartsWith("./", StringComparison.Ordinal) ? filename[2..] : filename)));
            }
        }

        var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var start = StripPrefix(path, repoRoot) is null ? 1 : repoRoot.Split('/', StringSplitOptions.RemoveEmptyEntries).Length + 1;
            for (var i = start; i < segments.Length; i++)
            {
                var candidate = string.Join('/', segments.Skip(i));
                if (File.Exists(Path.Combine(repoRoot, candidate)))
                {
                    var prefix = path[..^candidate.Length].TrimEnd('/');
                    votes[prefix] = votes.GetValueOrDefault(prefix) + 1;
                    break;
                }
            }
        }

        if (votes.Count > 0)
        {
            return votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key.Length).First().Key;
        }

        if (absoluteSources.Count == 0)
        {
            return null;
        }

        var common = absoluteSources[0].Split('/');
        foreach (var source in absoluteSources.Skip(1))
        {
            var parts = source.Split('/');
            var length = 0;
            while (length < common.Length && length < parts.Length && string.Equals(common[length], parts[length], StringComparison.OrdinalIgnoreCase))
            {
                length++;
            }

            common = common[..length];
        }

        var joined = string.Join('/', common).TrimEnd('/');
        return joined.Length > 0 && !(joined.Length == 2 && joined[1] == ':') ? joined : null;
    }

    private static string Normalize(string filename, string root, string anchor, List<string> sources)
    {
        var path = Slashes(filename);
        if (!IsAbsolute(path))
        {
            return UnderSource(path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path, root, anchor, sources);
        }

        if (!string.Equals(anchor, root, StringComparison.OrdinalIgnoreCase) && StripPrefix(path, anchor) is { } underAnchor)
        {
            return underAnchor;
        }

        if (StripPrefix(path, root) is { } underRoot)
        {
            return underRoot;
        }

        foreach (var source in sources)
        {
            if (StripPrefix(path, source.TrimEnd('/')) is { } underSource)
            {
                return underSource;
            }
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = string.Join('/', segments.Skip(i));
            if (File.Exists(Path.Combine(root, candidate)))
            {
                return candidate;
            }
        }

        return string.Join('/', segments.Where(s => !s.EndsWith(':')));
    }

    // coverlet writes filenames relative to <source>; re-anchor them to the repo root so they match diff paths.
    // Prefer the source the file actually exists under; otherwise the first source under the root.
    private static string UnderSource(string path, string root, string anchor, List<string> sources)
    {
        var candidates = sources
            .Select(source => SourcePrefix(source, anchor))
            .OfType<string>()
            .Select(prefix => prefix.Length == 0 ? path : $"{prefix}/{path}")
            .ToList();
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(root, c))) ?? candidates.FirstOrDefault() ?? path;
    }

    private static string? SourcePrefix(string source, string root)
    {
        var trimmed = source.TrimEnd('/');
        if (IsAbsolute(trimmed))
        {
            return string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase) ? "" : StripPrefix(trimmed, root);
        }

        while (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return trimmed == "." ? "" : trimmed;
    }

    private static string? StripPrefix(string path, string prefix) =>
        prefix.Length > 0 && path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ? path[(prefix.Length + 1)..] : null;

    private static bool IsAbsolute(string path) =>
        path.StartsWith('/') || (path.Length > 1 && path[1] == ':');

    private static string Slashes(string s) => s.Replace('\\', '/');
}
