using System.Xml;
using System.Xml.Linq;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

/// <summary>Report only (C24): project reference edges added or removed between base and head. A missing side means the project file was added or deleted. Never fails; the edges go to the report so CI can post them.</summary>
public static class ArchDeltaGate
{
    public static GateResult Evaluate(IReadOnlyDictionary<string, (string? Base, string? Head)> csprojs)
    {
        if (csprojs.Count == 0)
        {
            return new GateResult(GateId.ArchDelta, Verdict.NotApplicable, Message: "no project files changed");
        }

        var before = new HashSet<string>(StringComparer.Ordinal);
        var after = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, sides) in csprojs)
        {
            before.UnionWith(Edges(path, sides.Base));
            after.UnionWith(Edges(path, sides.Head));
        }

        var added = after.Except(before).Order(StringComparer.Ordinal).ToList();
        var removed = before.Except(after).Order(StringComparer.Ordinal).ToList();
        var offenders = added.Select(e => "+ " + e).Concat(removed.Select(e => "- " + e)).ToList();
        return new GateResult(
            GateId.ArchDelta,
            Verdict.Pass,
            Actual: offenders.Count,
            Offenders: offenders,
            Message: offenders.Count == 0 ? "project files changed, no reference edges added or removed" : $"{added.Count} project reference(s) added, {removed.Count} removed");
    }

    public static IReadOnlySet<string> Edges(string csprojPath, string? content)
    {
        var edges = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(content))
        {
            return edges;
        }

        var from = ProjectName(csprojPath);
        try
        {
            foreach (var reference in XDocument.Parse(content).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                if (reference.Attribute("Include")?.Value is { Length: > 0 } include)
                {
                    edges.Add($"{from} -> {ProjectName(include)}");
                }
            }
        }
        catch (XmlException)
        {
        }

        return edges;
    }

    private static string ProjectName(string path) => System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
}
