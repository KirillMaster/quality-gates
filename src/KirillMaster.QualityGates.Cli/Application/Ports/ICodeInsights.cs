using KirillMaster.QualityGates.Cli.Domain.Gates;

namespace KirillMaster.QualityGates.Cli.Application.Ports;

/// <summary>Syntax-level facts about one C# source text for the process gates. Implementations must not throw on malformed source.</summary>
public interface ICodeInsights
{
    /// <summary>Cognitive complexity per method, keyed by <c>Type.Method(parameter types)</c>.</summary>
    IReadOnlyDictionary<string, int> CognitiveComplexity(string source);

    /// <summary>Names of the public top-level types declared in the source.</summary>
    IReadOnlyList<string> PublicTypes(string source);

    /// <summary>Every identifier token of the source.</summary>
    IReadOnlySet<string> Identifiers(string source);

    /// <summary>Controller actions and minimal-API style endpoints with the injected-service calls they make.</summary>
    IReadOnlyList<EndpointFlow> Flows(string source);
}

/// <summary>Writes a text artifact (for example a Mermaid diagram) into the output directory and reads input text files.</summary>
public interface IArtifactWriter
{
    void Write(string directory, string fileName, string content);

    /// <summary>Returns the file content, or null when the file does not exist.</summary>
    string? Read(string path);
}

/// <summary>Reads type, reference and namespace-edge facts from compiled assemblies. A file that is not a managed assembly is skipped.</summary>
public interface IAssemblyReader
{
    IReadOnlyList<AssemblyFacts> Read(IReadOnlyList<string> files);
}

public interface IBranchInfo
{
    /// <summary>Commit time of the oldest commit on HEAD that is not on the target, or null when there is none.</summary>
    Task<DateTimeOffset?> FirstBranchCommitAsync(string repoPath, string target, CancellationToken cancellationToken = default);
}
