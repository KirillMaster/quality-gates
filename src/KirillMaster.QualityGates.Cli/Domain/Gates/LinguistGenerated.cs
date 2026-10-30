namespace KirillMaster.QualityGates.Cli.Domain.Gates;

/// <summary>Path patterns marked <c>linguist-generated</c> (set, or <c>=true</c>) in .gitattributes text; a later <c>-linguist-generated</c> or <c>=false</c> line for the same pattern removes it.</summary>
public static class LinguistGenerated
{
    public static IReadOnlyList<string> Patterns(string? gitattributes)
    {
        var patterns = new List<string>();
        foreach (var raw in (gitattributes ?? string.Empty).Split('\n'))
        {
            var parts = raw.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].StartsWith('#'))
            {
                continue;
            }

            foreach (var attribute in parts.Skip(1))
            {
                if (attribute is "linguist-generated" or "linguist-generated=true")
                {
                    patterns.Add(parts[0]);
                }
                else if (attribute is "-linguist-generated" or "linguist-generated=false")
                {
                    patterns.RemoveAll(p => p == parts[0]);
                }
            }
        }

        return patterns;
    }
}
