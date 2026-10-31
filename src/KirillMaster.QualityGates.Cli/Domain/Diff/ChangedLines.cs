namespace KirillMaster.QualityGates.Cli.Domain.Diff;

public sealed class ChangedLines
{
    private readonly Dictionary<string, SortedSet<int>> _files = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Files => _files.Keys;

    public bool IsEmpty => _files.Count == 0;

    public IReadOnlyCollection<int> LinesOf(string path) =>
        _files.TryGetValue(path, out var lines) ? lines : [];

    public void Add(string path, int line)
    {
        if (!_files.TryGetValue(path, out var lines))
        {
            lines = [];
            _files[path] = lines;
        }

        lines.Add(line);
    }
}
