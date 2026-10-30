using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using KirillMaster.QualityGates.Cli.Adapters.Sarif;
using KirillMaster.QualityGates.Cli.Application.Ports;
using KirillMaster.QualityGates.Cli.Domain.Grandfather;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace KirillMaster.QualityGates.Cli.Adapters.Roslyn;

public sealed class RoslynBannedCallScanner : IBannedCallScanner
{
    private const string Rule = "RS0030";

    private readonly Dictionary<string, CSharpCompilation> _compilations = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<BannedCallSite> Scan(string repoRoot, IReadOnlyList<string> sarifPaths)
    {
        var files = sarifPaths.Count == 0 ? ScanBuild(repoRoot) : ExpandSarif(sarifPaths);
        var sites = new HashSet<BannedCallSite>();

        foreach (var sarif in files)
        {
            var project = SarifReader.ProjectName(sarif);
            foreach (var result in SarifReader.ReadResults(sarif))
            {
                if (result.RuleId != Rule || result.Suppressed || !SarifReader.IsCounted(result.Level) || result.Uri is null)
                {
                    continue;
                }

                sites.Add(new BannedCallSite(project, MemberDocId(repoRoot, project, result)));
            }
        }

        var ordered = new List<BannedCallSite>(sites);
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.Project + "|" + a.MemberDocId, b.Project + "|" + b.MemberDocId));
        return ordered;
    }

    private static List<string> ExpandSarif(IReadOnlyList<string> entries)
    {
        var files = new List<string>();
        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                files.AddRange(Directory.EnumerateFiles(entry, "*.sarif", SearchOption.AllDirectories));
            }
            else if (File.Exists(entry))
            {
                files.Add(entry);
            }
            else
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(entry)) ?? ".";
                var pattern = Path.GetFileName(entry);
                if (!Directory.Exists(directory) || !pattern.Contains('*', StringComparison.Ordinal))
                {
                    throw new FileNotFoundException($"SARIF input not found: {entry}");
                }

                files.AddRange(Directory.EnumerateFiles(directory, pattern));
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static List<string> ScanBuild(string repoRoot)
    {
        RunScanBuild(repoRoot);

        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(repoRoot, "*.sarif", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/quality/", StringComparison.Ordinal))
            {
                files.Add(file);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void RunScanBuild(string repoRoot)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "build", repoRoot, "-p:QualityGatesGrandfatherScan=true", "-nologo", "-nodeReuse:false" })
        {
            psi.ArgumentList.Add(arg);
        }

        var log = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => AppendLine(log, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(log, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"scan build failed ({process.ExitCode}):\n{Tail(log.ToString())}");
        }
    }

    private static void AppendLine(StringBuilder log, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (log)
        {
            log.AppendLine(line);
        }
    }

    private static string Tail(string text) => text.Length <= 4000 ? text : text[^4000..];

    private string MemberDocId(string repoRoot, string project, SarifResult result)
    {
        var path = ToLocalPath(repoRoot, result.Uri!);
        var projectDir = FindProjectDirectory(path)
                         ?? throw new InvalidOperationException($"no .csproj found above {path}");
        var compilation = GetCompilation(projectDir, project);

        var tree = compilation.SyntaxTrees.FirstOrDefault(
                       candidate => string.Equals(candidate.FilePath, path, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"{path} is not part of project {project}");

        var text = tree.GetText();
        if (result.Line < 1 || result.Line > text.Lines.Count)
        {
            throw new InvalidOperationException($"{path}:{result.Line} is outside the file");
        }

        var position = text.Lines[result.Line - 1].Start + Math.Max(0, result.Column - 1);
        var node = tree.GetRoot().FindToken(Math.Min(position, tree.GetRoot().FullSpan.End - 1)).Parent;
        var model = compilation.GetSemanticModel(tree);

        var enclosing = EnclosingMember(model, node)
                        ?? throw new InvalidOperationException($"{path}:{result.Line}:{result.Column} is not inside a member that can be suppressed");
        if (UnresolvedType(enclosing) is { } unresolved)
        {
            throw new InvalidOperationException($"{path}:{result.Line}: type '{unresolved}' in the signature of {enclosing.GetDocumentationCommentId()} does not resolve, so its suppression id would be unqualified and would not apply; restore the project (dotnet restore) so its references resolve");
        }

        return enclosing.GetDocumentationCommentId()!;
    }

    private static string? UnresolvedType(ISymbol member)
    {
        var types = member switch
        {
            IMethodSymbol method => method.Parameters.Select(p => p.Type).Append(method.ReturnType),
            IPropertySymbol property => property.Parameters.Select(p => p.Type),
            _ => []
        };
        return types.Select(UnresolvedType).FirstOrDefault(name => name is not null);
    }

    private static string? UnresolvedType(ITypeSymbol type) => type switch
    {
        IErrorTypeSymbol error => error.Name,
        IArrayTypeSymbol array => UnresolvedType(array.ElementType),
        IPointerTypeSymbol pointer => UnresolvedType(pointer.PointedAtType),
        INamedTypeSymbol named => named.TypeArguments.Select(UnresolvedType).FirstOrDefault(name => name is not null),
        _ => null
    };

    private static ISymbol? EnclosingMember(SemanticModel model, SyntaxNode? node)
    {
        for (; node is not null; node = node.Parent)
        {
            if (!IsMemberDeclaration(node))
            {
                continue;
            }

            var symbol = model.GetDeclaredSymbol(node);
            if (symbol?.GetDocumentationCommentId() is not null)
            {
                return symbol;
            }
        }

        return null;
    }

    private static bool IsMemberDeclaration(SyntaxNode node) => node is
        BaseMethodDeclarationSyntax
        or BasePropertyDeclarationSyntax
        or VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax };

    private static string ToLocalPath(string repoRoot, string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute) && absolute.IsFile)
        {
            return Path.GetFullPath(absolute.LocalPath);
        }

        return Path.GetFullPath(Path.Combine(repoRoot, uri));
    }

    private static string? FindProjectDirectory(string filePath)
    {
        for (var directory = Path.GetDirectoryName(filePath); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (Directory.EnumerateFiles(directory, "*.csproj").Any())
            {
                return directory;
            }
        }

        return null;
    }

    private CSharpCompilation GetCompilation(string projectDir, string project)
    {
        if (_compilations.TryGetValue(projectDir, out var cached))
        {
            return cached;
        }

        var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>();
        foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsSource(projectDir, file))
            {
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, path: Path.GetFullPath(file)));
            }
        }

        var objDir = Path.Combine(projectDir, "obj");
        if (Directory.Exists(objDir))
        {
            foreach (var file in Directory.EnumerateFiles(objDir, "*.GlobalUsings.g.cs", SearchOption.AllDirectories))
            {
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, path: Path.GetFullPath(file)));
            }
        }

        var compilation = CSharpCompilation.Create(
            project,
            trees,
            References(projectDir, project),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        _compilations[projectDir] = compilation;
        return compilation;
    }

    private static bool IsSource(string projectDir, string file)
    {
        var relative = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
        return !relative.StartsWith("obj/", StringComparison.Ordinal)
               && !relative.StartsWith("bin/", StringComparison.Ordinal)
               && !relative.Contains("/obj/", StringComparison.Ordinal)
               && !relative.Contains("/bin/", StringComparison.Ordinal);
    }

    private static List<MetadataReference> References(string projectDir, string project)
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(Path.GetFileName(path)))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var bin = Path.Combine(projectDir, "bin");
        if (Directory.Exists(bin))
        {
            foreach (var path in Directory.EnumerateFiles(bin, "*.dll", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (string.Equals(Path.GetFileNameWithoutExtension(name), project, StringComparison.OrdinalIgnoreCase) || !seen.Add(name))
                {
                    continue;
                }

                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch (IOException)
                {
                    // unreadable binary: the symbol may degrade to an error type, never a hard failure
                }
            }
        }

        AddAssetReferences(projectDir, references, seen);
        return references;
    }

    /// <summary>Compile references of the restored project (obj/project.assets.json): package assemblies and ASP.NET Core shared-framework assemblies. Without them a type such as IServiceCollection is an error type and its doc id loses the namespace.</summary>
    private static void AddAssetReferences(string projectDir, List<MetadataReference> references, HashSet<string> seen)
    {
        var assets = Path.Combine(projectDir, "obj", "project.assets.json");
        if (!File.Exists(assets))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(assets));
        var root = document.RootElement;
        var folders = root.TryGetProperty("packageFolders", out var packageFolders) ? packageFolders.EnumerateObject().Select(f => f.Name).ToList() : [];
        var libraries = root.TryGetProperty("libraries", out var libs) ? libs : default;
        if (root.TryGetProperty("targets", out var targets) && targets.EnumerateObject().FirstOrDefault().Value is { ValueKind: JsonValueKind.Object } target)
        {
            foreach (var entry in target.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("compile", out var compile) || !libraries.TryGetProperty(entry.Name, out var library) || !library.TryGetProperty("path", out var packagePath))
                {
                    continue;
                }

                foreach (var assembly in compile.EnumerateObject().Where(a => a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    var file = folders.Select(f => Path.Combine(f, packagePath.GetString()!, assembly.Name)).FirstOrDefault(File.Exists);
                    AddReference(references, seen, file);
                }
            }
        }

        if (root.TryGetProperty("project", out var project) && project.GetRawText().Contains("\"Microsoft.AspNetCore.App\"", StringComparison.Ordinal))
        {
            var shared = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "Microsoft.AspNetCore.App"));
            var newest = Directory.Exists(shared) ? Directory.EnumerateDirectories(shared).OrderByDescending(FrameworkVersion).FirstOrDefault() : null;
            foreach (var file in newest is null ? [] : Directory.EnumerateFiles(newest, "*.dll"))
            {
                AddReference(references, seen, file);
            }
        }
    }

    internal static Version FrameworkVersion(string folder)
    {
        var name = Path.GetFileName(folder);
        var release = name.Split('-', 2)[0];
        return Version.TryParse(release, out var version) ? version : new Version(0, 0);
    }

    private static void AddReference(List<MetadataReference> references, HashSet<string> seen, string? file)
    {
        if (file is null || !seen.Add(Path.GetFileName(file)))
        {
            return;
        }

        try
        {
            references.Add(MetadataReference.CreateFromFile(file));
        }
        catch (IOException)
        {
            // unreadable binary: the symbol degrades to an error type, which the doc id check reports
        }
    }
}
