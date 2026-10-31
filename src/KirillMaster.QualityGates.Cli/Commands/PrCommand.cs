using System.CommandLine;
using KirillMaster.QualityGates.Cli.Adapters.Git;
using KirillMaster.QualityGates.Cli.Adapters.Json;
using KirillMaster.QualityGates.Cli.Adapters.Pr;
using KirillMaster.QualityGates.Cli.Adapters.Quality;
using KirillMaster.QualityGates.Cli.Adapters.Roslyn;
using KirillMaster.QualityGates.Cli.Adapters.Stryker;
using KirillMaster.QualityGates.Cli.Application.UseCases;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Commands;

internal static class PrCommand
{
    public const string Name = "pr";

    public static Command Build(Func<string, int> handler, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var repo = CommandOptions.Required("--repo", "Repo root");
        var target = CommandOptions.Required("--target", "Target branch (origin/develop)");
        var source = CommandOptions.Required("--source", "Source branch name");
        var coverage = CommandOptions.RequiredMany("--coverage", "Cobertura files (unit + integration globs)");
        var sarif = CommandOptions.RequiredMany("--sarif", "SARIF files glob");
        var integration = new Option<bool?>("--integration-enabled") { Description = "Default from gates.json" };
        var output = CommandOptions.Required("--out", "Output dir for quality-report.json + html");
        var branchBuild = new Option<bool>("--branch-build") { Description = "Branch build: format findings are reported (warn) instead of blocking" };
        var baselineGlobs = new Option<string[]>("--baseline-glob") { Description = "Repo-relative globs of baseline files checked by BaselineGrowth (default: quality/baseline.json, quality/**/*baseline*.json, **/*architecture-baseline*.json, quality/grandfathered/*.GlobalSuppressions.cs)", AllowMultipleArgumentsPerToken = true };
        var growthApprovedBy = new Option<string[]>("--growth-approved-by") { Description = "GitHub login of an APPROVED review on the PR (passed by CI, not read from the PR); must be listed in CODEOWNERS on the target branch and differ from --pr-author", AllowMultipleArgumentsPerToken = true };
        var prAuthor = new Option<string?>("--pr-author") { Description = "GitHub login of the PR author; required for --growth-approved-by to count" };
        var couplingCommits = new Option<int>("--coupling-commits") { Description = "Commits of target history for ChangeCoupling", DefaultValueFactory = _ => new CouplingOptions().Commits };
        var couplingConfidence = new Option<double>("--coupling-confidence") { Description = "Minimum co-change confidence for ChangeCoupling", DefaultValueFactory = _ => new CouplingOptions().MinConfidence };
        var couplingSupport = new Option<int>("--coupling-support") { Description = "Minimum co-change commits for ChangeCoupling", DefaultValueFactory = _ => new CouplingOptions().MinSupport };
        var prMaxLines = new Option<int>("--pr-max-lines") { Description = "PrSize warns above this many changed lines", DefaultValueFactory = _ => new PrSizeLimits().MaxLines };
        var prMaxFiles = new Option<int>("--pr-max-files") { Description = "PrSize warns above this many changed files", DefaultValueFactory = _ => new PrSizeLimits().MaxFiles };
        var couplingBlock = new Option<bool>("--coupling-block") { Description = "ChangeCoupling fails (instead of warning) for a new coupling cluster the PR does not cover" };
        var changeKind = new Option<string>("--change-kind") { Description = "behavioral (default), bugfix or structural: structural changes need no test diff and no diff coverage", DefaultValueFactory = _ => "behavioral" };
        var featureRoots = new Option<string[]>("--feature-root") { Description = "Globs of feature folders that need a README.md when added (default: **/Features/*)", AllowMultipleArgumentsPerToken = true };
        var newMethodComplexity = new Option<int>("--new-method-complexity") { Description = "CognitiveComplexityDelta: maximum cognitive complexity of a method added by the PR", DefaultValueFactory = _ => new ComplexityDeltaOptions().NewMethodLimit };
        var fanOutLimit = new Option<int>("--fanout-limit") { Description = "TransitiveFanOut warns above this many transitive project references", DefaultValueFactory = _ => TransitiveFanOutGate.DefaultLimit };
        var assemblies = new Option<string[]>("--assemblies") { Description = "Built assemblies at HEAD (globs) for the compiled ArchDelta and the main-sequence report", AllowMultipleArgumentsPerToken = true };
        var baseAssemblies = new Option<string[]>("--assemblies-base") { Description = "Assemblies built at the merge base (globs); enables the compiled edge delta", AllowMultipleArgumentsPerToken = true };
        var baseCoverage = new Option<string[]>("--base-coverage") { Description = "Cobertura files of the target branch (globs); enables moved-line coverage for --change-kind structural", AllowMultipleArgumentsPerToken = true };
        var baseCoverageCommit = new Option<string?>("--base-coverage-commit") { Description = "Commit the --base-coverage files were built at; must be the merge base (git merge-base <target> HEAD)" };
        var baseCoverageRoot = new Option<string?>("--base-coverage-root") { Description = "Checkout root the --base-coverage files were built in (the base worktree); default: detected from the report paths" };
        var buildHistory = new Option<string?>("--build-history") { Description = "CI build history JSON file (builds[number,result,duration]) for the build trend" };
        var maxBranchAge = new Option<int>("--max-branch-age") { Description = "BranchAge warns above this many days", DefaultValueFactory = _ => BranchAgeGate.DefaultMaxDays };
        var skipOptions = new SkipOptions();

        var command = new Command(Name, "Run the pull-request gates");
        foreach (var option in new Option[] { repo, target, source, coverage, sarif, integration, output, branchBuild, baselineGlobs, growthApprovedBy, prAuthor, couplingCommits, couplingConfidence, couplingSupport, prMaxLines, prMaxFiles, couplingBlock, changeKind, featureRoots, newMethodComplexity, fanOutLimit, assemblies, baseAssemblies, baseCoverage, baseCoverageCommit, baseCoverageRoot, buildHistory, maxBranchAge })
        {
            command.Options.Add(option);
        }

        skipOptions.AddTo(command);

        command.SetAction(parse =>
        {
            if (stdout is null || stderr is null)
            {
                return handler(Name);
            }

            var (groups, bypassedBy) = skipOptions.Read(parse);

            var request = new PrRequest(
                Path.GetFullPath(parse.GetValue(repo)!),
                parse.GetValue(target)!,
                parse.GetValue(source)!,
                parse.GetValue(coverage)!,
                parse.GetValue(sarif)!,
                Path.GetFullPath(parse.GetValue(output)!),
                parse.GetValue(integration),
                groups,
                bypassedBy,
                parse.GetValue(branchBuild),
                new PrDeltaOptions(
                    parse.GetValue(baselineGlobs),
                    parse.GetValue(growthApprovedBy),
                    new CouplingOptions(parse.GetValue(couplingCommits), parse.GetValue(couplingSupport), MinConfidence: parse.GetValue(couplingConfidence), BlockNewClusters: parse.GetValue(couplingBlock)),
                    new PrSizeLimits(parse.GetValue(prMaxLines), parse.GetValue(prMaxFiles)),
                    parse.GetValue(prAuthor)),
                new PrProcessOptions(
                    ParseKind(parse.GetValue(changeKind)!),
                    parse.GetValue(featureRoots),
                    new ComplexityDeltaOptions(parse.GetValue(newMethodComplexity)),
                    parse.GetValue(fanOutLimit),
                    Assemblies: parse.GetValue(assemblies),
                    BaseAssemblies: parse.GetValue(baseAssemblies),
                    BaseCoverage: parse.GetValue(baseCoverage),
                    BaseCoverageCommit: parse.GetValue(baseCoverageCommit),
                    BaseCoverageRoot: parse.GetValue(baseCoverageRoot),
                    BuildHistory: parse.GetValue(buildHistory),
                    MaxBranchAgeDays: parse.GetValue(maxBranchAge)));

            var outcome = CreateRunner().RunAsync(request).GetAwaiter().GetResult();
            ReportPrinter.Print(outcome.Report, stdout, stderr);
            return outcome.ExitCode;
        });

        return command;
    }

    private static ChangeKind ParseKind(string value) =>
        Enum.TryParse<ChangeKind>(value, ignoreCase: true, out var kind) ? kind : throw new ArgumentException($"unknown --change-kind '{value}' (expected behavioral, bugfix or structural)");

    private static PrRunner CreateRunner() => new(
        new JsonConfigStore(),
        new JsonBaselineStore(),
        new GitCliClient(),
        new CoberturaCoverageReader(),
        new SarifSource(),
        new CsprojProjectLocator(),
        new GlobFiles(),
        new JsonReportStore(),
        TimeProvider.System,
        new DotnetFormatChecker(new SystemProcessRunner()),
        new JscpdScanner(new SystemProcessRunner()),
        new JsonDuplicationBaselineStore(),
        new PrDeltaGates(new GitPrChangeSource()),
        new PrProcessGates(new GitPrChangeSource(), new RoslynCodeInsights(), new FileArtifactWriter(), TimeProvider.System,
            new PrProcessPorts(new MetadataAssemblyReader(), new GitBranchInfo(), new GlobFiles(), new CoberturaCoverageReader(), new GitCliClient())));
}
