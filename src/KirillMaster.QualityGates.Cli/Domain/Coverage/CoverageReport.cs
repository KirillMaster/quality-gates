namespace KirillMaster.QualityGates.Cli.Domain.Coverage;

public sealed class CoverageReport
{
    private readonly Dictionary<string, Dictionary<int, int>> _files = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Files => _files.Keys;

    public IReadOnlyDictionary<int, int> LinesOf(string path) =>
        _files.TryGetValue(path, out var lines) ? lines : new Dictionary<int, int>();

    public bool HasFile(string path) => _files.ContainsKey(path);

    public bool TryGetHits(string path, int line, out int hits)
    {
        hits = 0;
        return _files.TryGetValue(path, out var lines) && lines.TryGetValue(line, out hits);
    }

    public void AddFile(string path)
    {
        if (!_files.ContainsKey(path))
        {
            _files[path] = [];
        }
    }

    public void Record(string path, int line, int hits)
    {
        AddFile(path);
        var lines = _files[path];
        lines[line] = lines.TryGetValue(line, out var existing) ? Math.Max(existing, hits) : hits;
    }

    public static CoverageReport Merge(IEnumerable<CoverageReport> reports)
    {
        var merged = new CoverageReport();
        foreach (var report in reports)
        {
            foreach (var (path, lines) in report._files)
            {
                merged.AddFile(path);
                foreach (var (line, hits) in lines)
                {
                    merged.Record(path, line, hits);
                }
            }
        }

        return merged;
    }
}
