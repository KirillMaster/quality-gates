using System.Globalization;
using System.Text;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Adapters.Pr;

/// <summary>Markdown rendering of a run report so CI can post it as a PR comment: one row per gate, offenders (for ArchDelta the added and removed project references) listed under it.</summary>
public static class MarkdownReport
{
    public const string FileName = "quality-report.md";

    public static string Render(RunReport report)
    {
        var text = new StringBuilder();
        text.Append("## Quality gates\n\n");
        if (report.Error is not null)
        {
            return text.Append("Error: ").Append(report.Error).Append('\n').ToString();
        }

        text.Append("| Gate | Verdict | Details |\n|---|---|---|\n");
        foreach (var result in report.Results)
        {
            var layer = result.Layer is null ? string.Empty : $" ({result.Layer.ToString()!.ToLowerInvariant()})";
            text.Append(CultureInfo.InvariantCulture, $"| {result.Gate}{layer} | {result.Verdict.ToString().ToLowerInvariant()} | {(result.Message ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal)} |\n");
        }

        foreach (var result in report.Results.Where(r => r.Offenders is { Count: > 0 }))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n### {result.Gate}\n\n");
            foreach (var offender in result.Offenders!)
            {
                text.Append(CultureInfo.InvariantCulture, $"- `{offender}`\n");
            }
        }

        return text.ToString();
    }
}
