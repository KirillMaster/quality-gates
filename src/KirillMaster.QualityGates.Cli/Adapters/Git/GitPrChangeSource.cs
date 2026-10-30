using System.Diagnostics;
using System.Globalization;
using System.Text;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Gates;

namespace KirillMaster.QualityGates.Cli.Adapters.Git;

public sealed class GitPrChangeSource : IPrChangeSource
{
    private readonly Dictionary<(string Repo, string Target), string> _mergeBases = [];

    public async Task<IReadOnlyList<FileChange>> ChangesAsync(string repoPath, string target, CancellationToken cancellationToken = default)
    {
        var output = await GitCliClient.RunAsync(repoPath, ["diff", "--numstat", "-z", "-M", "--no-color", "--no-ext-diff", $"{target}...HEAD"], $"git diff --numstat {target}...HEAD", cancellationToken).ConfigureAwait(false);
        var tokens = output.Split('\0');
        var changes = new List<FileChange>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var fields = tokens[i].Split('\t', 3);
            if (fields.Length < 3)
            {
                continue;
            }

            var added = int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : 0;
            var deleted = int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 0;
            if (fields[2].Length == 0 && i + 2 < tokens.Length)
            {
                changes.Add(new FileChange(tokens[i + 2], tokens[i + 1], added, deleted));
                i += 2;
            }
            else
            {
                changes.Add(new FileChange(fields[2], null, added, deleted));
            }
        }

        return changes;
    }

    public async Task<string?> ReadAsync(string repoPath, string target, PrRevision revision, string path, CancellationToken cancellationToken = default)
    {
        var rev = await RevisionAsync(repoPath, target, revision, cancellationToken).ConfigureAwait(false);
        try
        {
            return await GitCliClient.RunAsync(repoPath, ["show", $"{rev}:{path}"], $"git show {rev}:{path}", cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsMissingPath(ex.Message))
        {
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, string?>> ReadManyAsync(string repoPath, string target, PrRevision revision, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var distinct = paths.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return result;
        }

        var rev = await RevisionAsync(repoPath, target, revision, cancellationToken).ConfigureAwait(false);
        if (revision == PrRevision.Target)
        {
            await GitCliClient.RunAsync(repoPath, ["rev-parse", "--verify", "--quiet", $"{rev}^{{commit}}"], $"git rev-parse {rev}", cancellationToken).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["LC_ALL"] = "C";
        psi.ArgumentList.Add("cat-file");
        psi.ArgumentList.Add("--batch");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            var writer = Task.Run(async () =>
            {
                foreach (var path in distinct)
                {
                    await process.StandardInput.WriteAsync($"{rev}:{path}\n".AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                process.StandardInput.Close();
            }, cancellationToken);

            using var output = new BufferedStream(process.StandardOutput.BaseStream, 81920);
            foreach (var path in distinct)
            {
                var header = ReadLine(output) ?? throw new InvalidOperationException($"git cat-file --batch ended early: {(await stderr.ConfigureAwait(false)).Trim()}");
                if (header.EndsWith(" missing", StringComparison.Ordinal))
                {
                    result[path] = null;
                    continue;
                }

                var parts = header.Split(' ');
                if (parts.Length != 3 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                {
                    throw new InvalidOperationException($"git cat-file --batch returned an unexpected header: {header}");
                }

                var content = new byte[size + 1];
                await output.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
                result[path] = parts[1] == "blob" ? Decode(content.AsSpan(0, size)) : null;
            }

            await writer.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git cat-file --batch failed ({process.ExitCode}): {(await stderr.ConfigureAwait(false)).Trim()}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> BaseFilesAsync(string repoPath, string target, CancellationToken cancellationToken = default)
    {
        var mergeBase = await MergeBaseAsync(repoPath, target, cancellationToken).ConfigureAwait(false);
        var output = await GitCliClient.RunAsync(repoPath, ["ls-tree", "-r", "-z", "--name-only", mergeBase], $"git ls-tree {mergeBase}", cancellationToken).ConfigureAwait(false);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public Task<string> DiffFromAsync(string repoPath, string commit, CancellationToken cancellationToken = default) =>
        GitCliClient.RunAsync(repoPath, ["diff", "-U0", "-M", "--no-color", "--no-ext-diff", commit, "HEAD"], $"git diff {commit} HEAD", cancellationToken);

    public async Task<IReadOnlyList<string>> HeadFilesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var output = await GitCliClient.RunAsync(repoPath, ["ls-tree", "-r", "-z", "--name-only", "HEAD"], "git ls-tree HEAD", cancellationToken).ConfigureAwait(false);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public async Task<IReadOnlyList<IReadOnlyList<string>>> HistoryAsync(string repoPath, string target, int count, CancellationToken cancellationToken = default)
    {
        var output = await GitCliClient.RunAsync(repoPath, ["log", "--no-merges", "-n", count.ToString(CultureInfo.InvariantCulture), "--name-only", "-z", "--format=%x01", target], $"git log {target}", cancellationToken).ConfigureAwait(false);
        return output.Split('\u0001', StringSplitOptions.RemoveEmptyEntries)
            .Select(block => (IReadOnlyList<string>)block.Split(['\0', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList())
            .ToList();
    }

    private async Task<string> RevisionAsync(string repoPath, string target, PrRevision revision, CancellationToken cancellationToken) =>
        revision switch
        {
            PrRevision.Head => "HEAD",
            PrRevision.Target => target,
            _ => await MergeBaseAsync(repoPath, target, cancellationToken).ConfigureAwait(false)
        };

    private static string? ReadLine(Stream stream)
    {
        var bytes = new List<byte>(64);
        int next;
        while ((next = stream.ReadByte()) >= 0)
        {
            if (next == '\n')
            {
                return Encoding.UTF8.GetString([.. bytes]);
            }

            bytes.Add((byte)next);
        }

        return null;
    }

    private static string Decode(ReadOnlySpan<byte> content) =>
        Encoding.UTF8.GetString(content.StartsWith(Encoding.UTF8.Preamble) ? content[Encoding.UTF8.Preamble.Length..] : content);

    private static bool IsMissingPath(string message) =>
        message.Contains("does not exist in", StringComparison.Ordinal) || message.Contains("exists on disk, but not in", StringComparison.Ordinal);

    private async Task<string> MergeBaseAsync(string repoPath, string target, CancellationToken cancellationToken)
    {
        if (!_mergeBases.TryGetValue((repoPath, target), out var mergeBase))
        {
            mergeBase = (await GitCliClient.RunAsync(repoPath, ["merge-base", target, "HEAD"], $"git merge-base {target} HEAD", cancellationToken).ConfigureAwait(false)).Trim();
            _mergeBases[(repoPath, target)] = mergeBase;
        }

        return mergeBase;
    }
}
