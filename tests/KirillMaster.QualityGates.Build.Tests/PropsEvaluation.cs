using System.Diagnostics;
using System.Text.Json;

namespace KirillMaster.QualityGates.Build.Tests;

/// <summary>Evaluates the buildTransitive props/targets in a throwaway project via <c>dotnet msbuild -getProperty</c>.</summary>
internal sealed class PropsEvaluation : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "qg-props", Guid.NewGuid().ToString("N"));

    public PropsEvaluation(string projectName = "Shop.Adapters", string properties = "")
    {
        ProjectName = projectName;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project />");
        var root = FindRepositoryRoot();
        File.Copy(Path.Combine(root, "global.json"), Path.Combine(directory, "global.json"));
        var transitive = Path.Combine(root, "src", "KirillMaster.QualityGates.Build", "buildTransitive");
        File.WriteAllText(
            Path.Combine(directory, projectName + ".csproj"),
            $"""
            <Project>
              <Import Project="{transitive}/KirillMaster.QualityGates.Build.props" />
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>{properties}</PropertyGroup>
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
              <Import Project="{transitive}/KirillMaster.QualityGates.Build.targets" />
            </Project>
            """);
    }

    public string ProjectName { get; }

    public void AddGrandfathered()
    {
        var folder = Path.Combine(directory, "quality", "grandfathered");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ProjectName + ".GlobalSuppressions.cs"), "// grandfathered");
    }

    public IReadOnlyDictionary<string, string> Properties(IEnumerable<string> names, IDictionary<string, string>? msbuild = null, IDictionary<string, string>? environment = null)
    {
        var list = names.ToArray();
        var arguments = new List<string> { "msbuild", ProjectName + ".csproj", "-nologo", "-t:QualityGatesResolveMode", "-getProperty:" + string.Join(',', list) };
        arguments.AddRange((msbuild ?? new Dictionary<string, string>()).Select(p => $"-p:{p.Key}={p.Value.Replace(";", "%3B", StringComparison.Ordinal)}"));
        using var json = JsonDocument.Parse(Run(arguments, environment));
        var values = json.RootElement.GetProperty("Properties");
        return list.ToDictionary(n => n, n => values.GetProperty(n).GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    public bool CompilesGrandfathered(IDictionary<string, string>? msbuild = null)
    {
        var arguments = new List<string> { "msbuild", ProjectName + ".csproj", "-nologo", "-getItem:Compile" };
        arguments.AddRange((msbuild ?? new Dictionary<string, string>()).Select(p => $"-p:{p.Key}={p.Value.Replace(";", "%3B", StringComparison.Ordinal)}"));
        using var json = JsonDocument.Parse(Run(arguments, null));
        return json.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
            .Any(i => i.GetProperty("Identity").GetString()!.EndsWith("GlobalSuppressions.cs", StringComparison.Ordinal));
    }

    public IReadOnlyList<string> GlobalConfigs(IDictionary<string, string>? msbuild = null)
    {
        var arguments = new List<string> { "msbuild", ProjectName + ".csproj", "-nologo", "-getItem:GlobalAnalyzerConfigFiles" };
        arguments.AddRange((msbuild ?? new Dictionary<string, string>()).Select(p => $"-p:{p.Key}={p.Value.Replace(";", "%3B", StringComparison.Ordinal)}"));
        using var json = JsonDocument.Parse(Run(arguments, null));
        return [.. json.RootElement.GetProperty("Items").GetProperty("GlobalAnalyzerConfigFiles").EnumerateArray().Select(i => Path.GetFileName(i.GetProperty("Identity").GetString()!))];
    }

    public IReadOnlyList<string> BannedSymbolFiles(IDictionary<string, string>? msbuild = null)
    {
        var arguments = new List<string> { "msbuild", ProjectName + ".csproj", "-nologo", "-getItem:AdditionalFiles" };
        arguments.AddRange((msbuild ?? new Dictionary<string, string>()).Select(p => $"-p:{p.Key}={p.Value.Replace(";", "%3B", StringComparison.Ordinal)}"));
        using var json = JsonDocument.Parse(Run(arguments, null));
        return [.. json.RootElement.GetProperty("Items").GetProperty("AdditionalFiles").EnumerateArray().Select(i => Path.GetFileName(i.GetProperty("Identity").GetString()!)).Where(n => n.StartsWith("BannedSymbols", StringComparison.Ordinal))];
    }

    public string Build(string source)
    {
        File.WriteAllText(Path.Combine(directory, "Source.cs"), source);
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "build", ProjectName + ".csproj", "-nologo", "-v:q", "-p:EnableDefaultCompileItems=true", "-p:TreatWarningsAsErrors=false", "-p:UseSharedCompilation=false" })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove("QUALITYGATES_MODE");
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var all = output + error.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, all);
        return all;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "QualityGates.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("QualityGates.sln not found above the test binaries.");
    }

    private string Run(IEnumerable<string> arguments, IDictionary<string, string>? environment)
    {
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove("QUALITYGATES_MODE");
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error.GetAwaiter().GetResult());
        return output;
    }
}
