using System.Globalization;
using System.Text.RegularExpressions;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Domain.Gates;

public sealed record ToggleMarker(string Path, int Line, string Text);

/// <summary>C30 (warn report): a feature toggle carries <c>qg-toggle-expiry: yyyy-MM-dd</c> in a comment next to its definition; an expired or unparseable date is reported.</summary>
public static partial class ToggleExpiryGate
{
    public const string Pattern = "qg-toggle-expiry:";

    public static GateResult Evaluate(IReadOnlyCollection<ToggleMarker> markers, DateOnly today)
    {
        if (markers.Count == 0)
        {
            return new GateResult(GateId.ToggleExpiry, Verdict.NotApplicable, Message: "no toggle expiry markers found");
        }

        var offenders = new List<string>();
        foreach (var marker in markers.OrderBy(m => m.Path, StringComparer.Ordinal).ThenBy(m => m.Line))
        {
            var match = MarkerRegex().Match(marker.Text);
            var where = $"{marker.Path}:{marker.Line.ToString(CultureInfo.InvariantCulture)}";
            if (!match.Success || !DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
            {
                offenders.Add($"{where}: toggle expiry is not a yyyy-MM-dd date");
            }
            else if (expiry < today)
            {
                offenders.Add($"{where}: toggle expired on {match.Groups[1].Value}");
            }
        }

        return offenders.Count == 0
            ? new GateResult(GateId.ToggleExpiry, Verdict.Pass, Actual: 0, Threshold: 0, Message: $"{markers.Count.ToString(CultureInfo.InvariantCulture)} toggle(s) within their expiry date")
            : new GateResult(GateId.ToggleExpiry, Verdict.Warn, Actual: offenders.Count, Threshold: 0, Offenders: offenders, Message: $"{offenders.Count.ToString(CultureInfo.InvariantCulture)} expired or malformed toggle marker(s); remove the toggle or move the date with a reason");
    }

    [GeneratedRegex(@"qg-toggle-expiry:\s*(\S+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex MarkerRegex();
}
