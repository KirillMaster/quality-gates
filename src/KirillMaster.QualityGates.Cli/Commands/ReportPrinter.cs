using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Commands;

internal static class ReportPrinter
{
    public static void Print(RunReport report, TextWriter stdout, TextWriter stderr)
    {
        if (report.Error is not null)
        {
            stderr.WriteLine(report.Error);
            return;
        }

        foreach (var result in report.Results)
        {
            var layer = result.Layer is null ? string.Empty : $" [{result.Layer.ToString()!.ToLowerInvariant()}]";
            var detail = result.Message ?? (result.Actual is null ? string.Empty : $"{result.Actual:0.##} (threshold {result.Threshold:0.##})");
            stdout.WriteLine($"{result.Gate}{layer}: {result.Verdict.ToString().ToLowerInvariant()} {detail}".TrimEnd());
            foreach (var offender in result.Offenders ?? [])
            {
                stdout.WriteLine($"  {offender}");
            }
        }

        if (report.BypassedBy is not null)
        {
            stdout.WriteLine($"bypassed by {report.BypassedBy}");
        }
    }
}
