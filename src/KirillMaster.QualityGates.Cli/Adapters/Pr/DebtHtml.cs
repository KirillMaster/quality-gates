using System.Globalization;
using System.Net;
using System.Text;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Adapters.Pr;

public static class DebtHtml
{
    public const string FileName = "debt.html";

    public static string Render(DebtReport report)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Static analysis debt</title>")
            .Append("<style>body{font-family:sans-serif;margin:2em}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:2px 8px;text-align:left}code{font-size:90%}</style>")
            .Append("</head><body><h1>Static analysis debt</h1>");

        var current = Total(report.Current);
        var baseline = report.Baseline is null ? "n/a" : Total(report.Baseline).ToString(CultureInfo.InvariantCulture);
        html.Append(CultureInfo.InvariantCulture, $"<p>Total warnings: {current} (baseline: {baseline})</p>");

        var byProject = report.Findings
            .GroupBy(f => f.Project, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var project in byProject)
        {
            var before = report.Baseline?.GetValueOrDefault(project.Key);
            html.Append("<h2>").Append(Encode(project.Key)).Append("</h2><table><tr><th>Rule</th><th>Count</th><th>Baseline</th></tr>");
            var rules = project.GroupBy(f => f.RuleId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            foreach (var rule in rules)
            {
                var allowed = before is not null && before.TryGetValue(rule.Key, out var n) ? n.ToString(CultureInfo.InvariantCulture) : "0";
                html.Append(CultureInfo.InvariantCulture, $"<tr><td>{Encode(rule.Key)}</td><td>{rule.Count()}</td><td>{allowed}</td></tr>");
            }

            html.Append("</table>");
            foreach (var rule in rules)
            {
                html.Append(CultureInfo.InvariantCulture, $"<details><summary>{Encode(rule.Key)} ({rule.Count()})</summary><ul>");
                foreach (var finding in rule.OrderBy(f => f.Uri, StringComparer.Ordinal).ThenBy(f => f.Line))
                {
                    html.Append(CultureInfo.InvariantCulture, $"<li><code>{Encode(finding.Uri)}:{finding.Line}</code></li>");
                }

                html.Append("</ul></details>");
            }
        }

        return html.Append("</body></html>").ToString();
    }

    private static int Total(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> warnings) =>
        warnings.Values.Sum(rules => rules.Values.Sum());

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
