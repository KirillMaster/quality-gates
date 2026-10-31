using KirillMaster.QualityGates.Cli.Application.Ports;

namespace KirillMaster.QualityGates.Cli.Adapters.Stryker;

public sealed class StrykerRunner(IProcessRunner? processes = null) : IMutationRunner
{
    private const string ReportName = "mutation-report.json";
    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", "StrykerOutput", ".git"];

    private readonly IProcessRunner _processes = processes ?? new SystemProcessRunner();

    public async Task<MutationRun> RunAsync(string repoPath, string targetBranch, CancellationToken cancellationToken = default)
    {
        var repo = Path.GetFullPath(repoPath);
        var before = FindReports(repo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var command = new ProcessCommand("dotnet", BuildArguments(repo, targetBranch), repo);

        ProcessResult process;
        try
        {
            process = await _processes.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MutationRun.BudgetExceeded;
        }

        var report = FindReports(repo)
            .Where(r => !before.Contains(r))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (report is null)
        {
            throw new MutationReportException(
                $"stryker (exit code {process.ExitCode}) did not write a new {ReportName}; expected {Path.Combine(repo, "StrykerOutput", "<timestamp>", "reports", ReportName)}. Output tail: {process.OutputTail}");
        }

        var json = await File.ReadAllTextAsync(report, CancellationToken.None).ConfigureAwait(false);
        return MutationRun.Completed(MutationReportParser.Parse(json, repo, report));
    }

    /// <remarks>
    /// <c>--with-baseline</c> is not passed: dotnet-stryker 4.x rejects it together with <c>--since</c>
    /// ("The since and baseline features are mutually exclusive").
    /// </remarks>
    internal static List<string> BuildArguments(string repo, string targetBranch)
    {
        var args = new List<string> { "stryker", $"--since:{targetBranch}", "--reporter", "json" };

        var solution = Directory.EnumerateFiles(repo, "*.sln").Order(StringComparer.Ordinal).FirstOrDefault();
        if (solution is not null)
        {
            args.Add("--solution");
            args.Add(Path.GetFileName(solution));
        }

        foreach (var project in UnitTestProjects(repo))
        {
            args.Add("--test-project");
            args.Add(Path.GetRelativePath(repo, project).Replace('\\', '/'));
        }

        return args;
    }

    private static IEnumerable<string> UnitTestProjects(string repo) =>
        EnumerateProjects(repo)
            .Where(p => IsUnitTestProject(Path.GetFileNameWithoutExtension(p)))
            .Order(StringComparer.Ordinal);

    private static bool IsUnitTestProject(string name) =>
        (name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase))
        && !name.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateProjects(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.csproj"))
        {
            yield return file;
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in EnumerateProjects(sub))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> FindReports(string repo)
    {
        var output = Path.Combine(repo, "StrykerOutput");
        return Directory.Exists(output)
            ? Directory.EnumerateFiles(output, ReportName, SearchOption.AllDirectories)
            : [];
    }
}
