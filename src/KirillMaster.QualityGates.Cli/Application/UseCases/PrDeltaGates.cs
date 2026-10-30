using System.Text.Json;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Gates;
using KirillMaster.QualityGates.Cli.Domain.Model;

namespace KirillMaster.QualityGates.Cli.Application.UseCases;

public sealed record PrDeltaOptions(
    IReadOnlyList<string>? BaselineGlobs = null,
    IReadOnlyList<string>? ApprovedBy = null,
    CouplingOptions? Coupling = null,
    PrSizeLimits? Size = null,
    string? Author = null)
{
    public static readonly IReadOnlyList<string> DefaultBaselineGlobs =
    [
        "quality/baseline.json",
        "quality/**/*baseline*.json",
        "**/*architecture-baseline*.json",
        "quality/grandfathered/*.GlobalSuppressions.cs"
    ];
}

/// <summary>Input the gates cannot evaluate (for example a malformed baseline file): the run ends with an error report and exit code 3.</summary>
public sealed class PrInputException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The gates computed from the pull-request delta alone: BaselineGrowth (blocking), ChangeCoupling and PrSize (warn), ArchDelta (report only).</summary>
public sealed class PrDeltaGates(IPrChangeSource source)
{
    private static readonly string[] CodeOwnersPaths = [".github/CODEOWNERS", "CODEOWNERS", "docs/CODEOWNERS"];

    public async Task<IReadOnlyList<GateResult>> EvaluateAsync(PrRequest request, CancellationToken cancellationToken = default)
    {
        var skipped = request.Skip ?? [];
        var options = request.Delta ?? new PrDeltaOptions();
        if (new[] { QualityGroup.BaselineGrowth, QualityGroup.Coupling, QualityGroup.PrSize, QualityGroup.ArchDelta }.All(skipped.Contains))
        {
            return [Bypassed(GateId.BaselineGrowth), Bypassed(GateId.ChangeCoupling), Bypassed(GateId.PrSize), Bypassed(GateId.ArchDelta)];
        }

        IReadOnlyList<FileChange> changes = [];
        string? changesError = null;
        try
        {
            changes = await source.ChangesAsync(request.Repo, request.Target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            changesError = ex.Message;
        }

        return
        [
            skipped.Contains(QualityGroup.BaselineGrowth) ? Bypassed(GateId.BaselineGrowth) : await BlockingAsync(GateId.BaselineGrowth, changesError, () => BaselineGrowthAsync(request, options, changes, cancellationToken)).ConfigureAwait(false),
            skipped.Contains(QualityGroup.Coupling) ? Bypassed(GateId.ChangeCoupling) : await AdvisoryAsync(GateId.ChangeCoupling, changesError, () => CouplingAsync(request, options, changes, cancellationToken)).ConfigureAwait(false),
            skipped.Contains(QualityGroup.PrSize) ? Bypassed(GateId.PrSize) : await AdvisoryAsync(GateId.PrSize, changesError, () => SizeAsync(request, options, changes, cancellationToken)).ConfigureAwait(false),
            skipped.Contains(QualityGroup.ArchDelta) ? Bypassed(GateId.ArchDelta) : await AdvisoryAsync(GateId.ArchDelta, changesError, () => ArchDeltaAsync(request, changes, cancellationToken)).ConfigureAwait(false)
        ];
    }

    /// <summary>A git or IO failure in a warn-only or report-only gate becomes a warning on that gate, so it can neither fail the run nor hide the other results.</summary>
    private static async Task<GateResult> AdvisoryAsync(GateId gate, string? changesError, Func<Task<GateResult>> run)
    {
        if (changesError is not null)
        {
            return new GateResult(gate, Verdict.Warn, Message: $"{gate} could not run: {changesError}");
        }

        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new GateResult(gate, Verdict.Warn, Message: $"{gate} could not run: {ex.Message}");
        }
    }

    /// <summary>A git or IO failure in the blocking gate is a tool error for the run (exit 3); the runner still writes the report.</summary>
    private static async Task<GateResult> BlockingAsync(GateId gate, string? changesError, Func<Task<GateResult>> run)
    {
        if (changesError is not null)
        {
            throw new PrInputException($"{gate} could not run: {changesError}");
        }

        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            throw new PrInputException($"{gate} could not run: {ex.Message}", ex);
        }
    }

    private async Task<GateResult> BaselineGrowthAsync(PrRequest request, PrDeltaOptions options, IReadOnlyList<FileChange> changes, CancellationToken cancellationToken)
    {
        var globs = options.BaselineGlobs is { Count: > 0 } custom ? custom : PrDeltaOptions.DefaultBaselineGlobs;
        var groups = new SortedDictionary<string, (Dictionary<string, int> Base, Dictionary<string, int> Head)>(StringComparer.Ordinal);
        var removed = new List<string>();
        foreach (var change in changes.Where(c => PathGlob.IsMatchAny(globs, c.Path) || (c.OldPath is not null && PathGlob.IsMatchAny(globs, c.OldPath))))
        {
            var sourcePath = change.OldPath ?? change.Path;
            var inGlobs = PathGlob.IsMatchAny(globs, change.Path);
            var groupPath = inGlobs ? change.Path : sourcePath;
            var group = BaselineCounts.IsSuppressionsFile(groupPath) ? BaselineCounts.SuppressionsGroup : groupPath;
            var before = await source.ReadAsync(request.Repo, request.Target, PrRevision.Base, sourcePath, cancellationToken).ConfigureAwait(false);
            var after = inGlobs ? await source.ReadAsync(request.Repo, request.Target, PrRevision.Head, change.Path, cancellationToken).ConfigureAwait(false) : null;
            if (before is not null && (!inGlobs || (after is null && !BaselineCounts.IsSuppressionsFile(groupPath))))
            {
                removed.Add(groupPath);
            }

            if (!groups.TryGetValue(group, out var sides))
            {
                sides = ([], []);
                groups[group] = sides;
            }

            Merge(sides.Base, ParseBaseline(sourcePath, before));
            Merge(sides.Head, ParseBaseline(change.Path, after));
        }

        var deltas = groups.Select(g => new BaselineDelta(g.Key, g.Value.Base, g.Value.Head)).ToList();
        var result = BaselineGrowthGate.Evaluate(deltas, [], new HashSet<string>(), removedBaselines: removed);
        if (result.Verdict != Verdict.Fail)
        {
            return result;
        }

        var approvers = (options.ApprovedBy ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return BaselineGrowthGate.Evaluate(deltas, approvers, await OwnersAsync(request, cancellationToken).ConfigureAwait(false), options.Author, removed);
    }

    private async Task<IReadOnlySet<string>> OwnersAsync(PrRequest request, CancellationToken cancellationToken)
    {
        foreach (var path in CodeOwnersPaths)
        {
            if (await source.ReadAsync(request.Repo, request.Target, PrRevision.Target, path, cancellationToken).ConfigureAwait(false) is { } text)
            {
                return CodeOwners.Logins(text);
            }
        }

        return new HashSet<string>();
    }

    private async Task<GateResult> CouplingAsync(PrRequest request, PrDeltaOptions options, IReadOnlyList<FileChange> changes, CancellationToken cancellationToken)
    {
        var coupling = options.Coupling ?? new CouplingOptions();
        var history = await source.HistoryAsync(request.Repo, request.Target, coupling.Commits, cancellationToken).ConfigureAwait(false);
        var present = (await source.HeadFilesAsync(request.Repo, cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        return ChangeCouplingGate.Evaluate(changes.Select(c => c.Path).ToList(), history, present.Contains, coupling);
    }

    private async Task<GateResult> SizeAsync(PrRequest request, PrDeltaOptions options, IReadOnlyList<FileChange> changes, CancellationToken cancellationToken)
    {
        var attributes = await source.ReadAsync(request.Repo, request.Target, PrRevision.Target, ".gitattributes", cancellationToken).ConfigureAwait(false);
        return PrSizeGate.Evaluate(changes, LinguistGenerated.Patterns(attributes), options.Size ?? new PrSizeLimits());
    }

    private async Task<GateResult> ArchDeltaAsync(PrRequest request, IReadOnlyList<FileChange> changes, CancellationToken cancellationToken)
    {
        var projects = new Dictionary<string, (string? Base, string? Head)>(StringComparer.Ordinal);
        foreach (var change in changes.Where(c => c.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var before = await source.ReadAsync(request.Repo, request.Target, PrRevision.Base, change.OldPath ?? change.Path, cancellationToken).ConfigureAwait(false);
            var after = await source.ReadAsync(request.Repo, request.Target, PrRevision.Head, change.Path, cancellationToken).ConfigureAwait(false);
            projects[change.Path] = (before, after);
        }

        return ArchDeltaGate.Evaluate(projects);
    }

    private static IReadOnlyDictionary<string, int> ParseBaseline(string path, string? content)
    {
        try
        {
            return BaselineCounts.Parse(path, content);
        }
        catch (JsonException ex)
        {
            throw new PrInputException($"malformed JSON in baseline file {path}: {ex.Message}", ex);
        }
    }

    private static void Merge(Dictionary<string, int> into, IReadOnlyDictionary<string, int> counts)
    {
        foreach (var (key, count) in counts)
        {
            into[key] = into.GetValueOrDefault(key) + count;
        }
    }

    private static GateResult Bypassed(GateId gate) => new(gate, Verdict.Skip, Message: PrRunner.BypassedMessage);
}
