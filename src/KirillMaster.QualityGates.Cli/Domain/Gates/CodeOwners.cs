namespace KirillMaster.QualityGates.Cli.Domain.Gates;

/// <summary>GitHub logins listed in a CODEOWNERS file (without <c>@</c>, compared case-insensitively); team handles (<c>@org/team</c>) and e-mail owners are not logins and are ignored.</summary>
public static class CodeOwners
{
    public static IReadOnlySet<string> Logins(string? text)
    {
        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            foreach (var token in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                if (token.Length > 1 && token[0] == '@' && !token.Contains('/', StringComparison.Ordinal))
                {
                    logins.Add(token[1..]);
                }
            }
        }

        return logins;
    }
}
