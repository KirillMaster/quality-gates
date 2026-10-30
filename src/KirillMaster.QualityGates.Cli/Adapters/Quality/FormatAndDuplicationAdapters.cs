using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KirillMaster.QualityGates.Cli.Adapters.Stryker;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Gates;

namespace KirillMaster.QualityGates.Cli.Adapters.Quality;

/// <summary>Runs <c>dotnet format whitespace --folder --verify-no-changes</c> over the whole repo (no solution load; analyzers are gated by the static ratchet) and reads its JSON report.</summary>
public sealed class DotnetFormatChecker(IProcessRunner processes) : IFormatChecker
{
    public static readonly string[] Exclude = ["**/Migrations/**", "**/obj/**", "**/bin/**", "**/node_modules/**", "**/*.Designer.cs", "**/*.g.cs", "**/*.generated.cs"];

    public async Task<IReadOnlyList<string>> UnformattedFilesAsync(string repo, CancellationToken cancellationToken)
    {
        var reportDir = Path.Combine(Path.GetTempPath(), "qg-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportDir);
        try
        {
            var result = await processes.RunAsync(
                new ProcessCommand("dotnet", ["format", "whitespace", repo, "--folder", "--verify-no-changes", "--report", reportDir, "--exclude", .. Exclude], repo),
                cancellationToken).ConfigureAwait(false);
            var reportFile = Path.Combine(reportDir, "format-report.json");
            if (File.Exists(reportFile))
            {
                return ParseReport(File.ReadAllText(reportFile), repo).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            }

            return result.ExitCode == 0 ? [] : throw new InvalidOperationException($"dotnet format whitespace failed (exit {result.ExitCode}): {result.OutputTail}");
        }
        finally
        {
            Directory.Delete(reportDir, recursive: true);
        }
    }

    /// <summary>The report is a JSON array of <c>{ FilePath, FileChanges }</c>; paths are made repo-relative with forward slashes.</summary>
    public static IReadOnlyList<string> ParseReport(string json, string repo)
    {
        using var document = JsonDocument.Parse(json);
        var root = Path.GetFullPath(repo).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var paths = new List<string>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("FilePath", out var path) && path.GetString() is { Length: > 0 } value)
            {
                var full = Path.GetFullPath(value);
                var relative = full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : value;
                paths.Add(relative.Replace('\\', '/'));
            }
        }

        return paths;
    }
}

/// <summary>Runs jscpd (pinned) over C# sources, excluding tests, generated code and migrations, and fingerprints each clone.</summary>
public sealed class JscpdScanner(IProcessRunner processes) : IDuplicationScanner
{
    public const string JscpdVersion = "4.0.5";
    public const string Ignore = "**/Tests/**,**/*Tests*/**,**/Migrations/**,**/obj/**,**/bin/**,**/*.Designer.cs,**/*.g.cs,**/*.generated.cs,**/node_modules/**";

    public async Task<IReadOnlyList<DuplicateBlock>> ScanAsync(string repo, CancellationToken cancellationToken)
    {
        var output = Path.Combine(Path.GetTempPath(), "qg-jscpd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await processes.RunAsync(
                new ProcessCommand(
                    "npx",
                    ["--yes", $"jscpd@{JscpdVersion}", "--format", "csharp", "--min-lines", "15", "--min-tokens", "100", "--ignore", Ignore, "--reporters", "json", "--output", output, "--silent", repo],
                    repo),
                cancellationToken).ConfigureAwait(false);
            var report = Path.Combine(output, "jscpd-report.json");
            if (!File.Exists(report))
            {
                throw new InvalidOperationException($"jscpd produced no report (exit {result.ExitCode}): {result.OutputTail}");
            }

            return ParseReport(File.ReadAllText(report), repo);
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    public static IReadOnlyList<DuplicateBlock> ParseReport(string json, string repo)
    {
        using var document = JsonDocument.Parse(json);
        var blocks = new List<DuplicateBlock>();
        if (!document.RootElement.TryGetProperty("duplicates", out var duplicates))
        {
            return blocks;
        }

        foreach (var clone in duplicates.EnumerateArray())
        {
            var first = clone.GetProperty("firstFile");
            var second = clone.GetProperty("secondFile");
            var fragment = clone.TryGetProperty("fragment", out var f) ? f.GetString() ?? string.Empty : string.Empty;
            blocks.Add(new DuplicateBlock(
                Fingerprint(fragment),
                Relative(first.GetProperty("name").GetString() ?? string.Empty, repo),
                first.TryGetProperty("startLoc", out var firstLoc) && firstLoc.TryGetProperty("line", out var firstLine) ? firstLine.GetInt32() : first.GetProperty("start").GetInt32(),
                Relative(second.GetProperty("name").GetString() ?? string.Empty, repo),
                second.TryGetProperty("startLoc", out var secondLoc) && secondLoc.TryGetProperty("line", out var secondLine) ? secondLine.GetInt32() : second.GetProperty("start").GetInt32(),
                clone.TryGetProperty("lines", out var lines) ? lines.GetInt32() : 0));
        }

        return blocks;
    }

    public static string Fingerprint(string fragment)
    {
        var normalised = string.Concat(fragment.Where(c => !char.IsWhiteSpace(c)));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalised))).ToLowerInvariant();
    }

    private static string Relative(string path, string repo)
    {
        var root = Path.GetFullPath(repo).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(repo, path));
        return (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : path).Replace('\\', '/');
    }
}

public sealed class JsonDuplicationBaselineStore : IDuplicationBaselineStore
{
    public IReadOnlySet<string>? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("fingerprints").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
    }

    public void Save(string path, IReadOnlyCollection<string> fingerprints)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { fingerprints }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
