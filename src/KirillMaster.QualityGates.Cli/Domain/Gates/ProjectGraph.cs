namespace KirillMaster.QualityGates.Cli.Domain.Gates;

/// <summary>Project reference graph built from csproj contents: project name to its direct references.</summary>
public static class ProjectGraph
{
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Build(IReadOnlyDictionary<string, string> csprojs)
    {
        var graph = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var (path, content) in csprojs)
        {
            var name = NameOf(path);
            var refs = ArchDeltaGate.Edges(path, content).Select(e => e[(e.IndexOf("-> ", StringComparison.Ordinal) + 3)..]).ToHashSet(StringComparer.Ordinal);
            graph[name] = graph.TryGetValue(name, out var existing) ? existing.Union(refs).ToHashSet(StringComparer.Ordinal) : refs;
        }

        return graph;
    }

    public static string NameOf(string csprojPath) => System.IO.Path.GetFileNameWithoutExtension(csprojPath.Replace('\\', '/').Split('/')[^1]);

    public static IReadOnlySet<string> Transitive(IReadOnlyDictionary<string, IReadOnlySet<string>> graph, string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([project]);
        while (stack.Count > 0)
        {
            if (!graph.TryGetValue(stack.Pop(), out var direct))
            {
                continue;
            }

            foreach (var next in direct.Where(n => !string.Equals(n, project, StringComparison.Ordinal) && seen.Add(n)))
            {
                stack.Push(next);
            }
        }

        return seen;
    }
}
