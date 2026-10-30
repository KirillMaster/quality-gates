using System.Text.Json;
using System.Text.Json.Serialization;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Adapters.Pr;

public sealed class JsonReportStore : IReportStore
{
    public const string FileName = "quality-report.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    public void Write(string outDirectory, RunReport report) => Write(outDirectory, report, FileName);

    public void Write(string outDirectory, RunReport report, string fileName)
    {
        Directory.CreateDirectory(outDirectory);
        var dto = new ReportDto(
            report.Mode,
            report.Policy,
            report.SourceBranch,
            report.TargetBranch,
            report.ExitCode,
            report.BypassedBy,
            report.SkippedGroups,
            report.Error,
            report.Results);
        File.WriteAllText(Path.Combine(outDirectory, fileName), JsonSerializer.Serialize(dto, Options));
        if (fileName == FileName)
        {
            File.WriteAllText(Path.Combine(outDirectory, MarkdownReport.FileName), MarkdownReport.Render(report));
        }
    }

    public void WriteDebt(string outDirectory, DebtReport report)
    {
        Directory.CreateDirectory(outDirectory);
        File.WriteAllText(Path.Combine(outDirectory, DebtHtml.FileName), DebtHtml.Render(report));
    }

    private sealed record ReportDto(
        RunMode Mode,
        Policy Policy,
        string? SourceBranch,
        string? TargetBranch,
        int ExitCode,
        string? BypassedBy,
        IReadOnlyList<string>? Skipped,
        string? Error,
        IReadOnlyList<GateResult> Results);
}
