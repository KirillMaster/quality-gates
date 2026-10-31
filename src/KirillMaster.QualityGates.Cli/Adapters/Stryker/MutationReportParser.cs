using System.Text.Json;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Adapters.Stryker;

/// <summary>Reads the Stryker json report: files[*].mutants[*] with status, mutatorName and location.start.line.</summary>
public static class MutationReportParser
{
    public static IReadOnlyList<MutantResult> Parse(string json, string repoPath, string source = "mutation-report.json")
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ReadFiles(doc.RootElement, repoPath, source);
        }
        catch (JsonException ex)
        {
            throw new MutationReportException($"{source}: invalid json ({ex.Message})", ex);
        }
    }

    private static List<MutantResult> ReadFiles(JsonElement root, string repoPath, string source)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
        {
            throw new MutationReportException($"{source}: missing \"files\" object");
        }

        var result = new List<MutantResult>();
        foreach (var file in files.EnumerateObject())
        {
            var path = Relativize(file.Name, repoPath);
            if (file.Value.ValueKind != JsonValueKind.Object
                || !file.Value.TryGetProperty("mutants", out var mutants)
                || mutants.ValueKind != JsonValueKind.Array)
            {
                throw new MutationReportException($"{source}: file {file.Name} has no \"mutants\" array");
            }

            foreach (var mutant in mutants.EnumerateArray())
            {
                result.Add(ReadMutant(mutant, path, source));
            }
        }

        return result;
    }

    private static MutantResult ReadMutant(JsonElement mutant, string path, string source)
    {
        if (mutant.ValueKind != JsonValueKind.Object
            || !mutant.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String)
        {
            throw new MutationReportException($"{source}: a mutant in {path} has no \"status\"");
        }

        if (!TryReadStartLine(mutant, out var lineNumber))
        {
            throw new MutationReportException($"{source}: a mutant in {path} has no location.start.line");
        }

        return new MutantResult(path, lineNumber, ReadMutatorName(mutant), ParseStatus(status.GetString()!));
    }

    private static bool TryReadStartLine(JsonElement mutant, out int line)
    {
        line = 0;
        return mutant.TryGetProperty("location", out var location)
            && location.TryGetProperty("start", out var start)
            && start.TryGetProperty("line", out var lineElement)
            && lineElement.TryGetInt32(out line);
    }

    private static string ReadMutatorName(JsonElement mutant) =>
        mutant.TryGetProperty("mutatorName", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()!
            : "unknown mutator";

    private static MutantStatus ParseStatus(string status) => status switch
    {
        "Killed" => MutantStatus.Killed,
        "Timeout" => MutantStatus.Timeout,
        "Survived" => MutantStatus.Survived,
        "NoCoverage" => MutantStatus.NoCoverage,
        "Ignored" => MutantStatus.Ignored,
        "CompileError" => MutantStatus.CompileError,
        "RuntimeError" => MutantStatus.RuntimeError,
        _ => MutantStatus.Other
    };

    private static string Relativize(string reported, string repoPath)
    {
        var file = reported.Replace('\\', '/');
        var root = Path.GetFullPath(repoPath).Replace('\\', '/').TrimEnd('/') + "/";
        return file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? file[root.Length..] : file;
    }
}
