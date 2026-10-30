using System.Text;
using FluentAssertions;
using KirillMaster.QualityGates.Cli.Adapters.Roslyn;
using KirillMaster.QualityGates.Cli.Domain.Grandfather;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KirillMaster.QualityGates.Cli.Tests;

public sealed class RoslynBannedCallScannerTests : IDisposable
{
    private const string RepoSource = """
        using System;
        namespace Demo;
        public class Repo
        {
            private int _field = Banned();
            public int Prop => Banned();
            public int Method()
            {
                return Banned();
            }
            private static int Banned() => 1;
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("qg-scan-").FullName;

    public RoslynBannedCallScannerTests()
    {
        AddProject("Sample");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string AddProject(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(dir, "Repo.cs"), RepoSource);
        return dir;
    }

    private static string[] Lines() => RepoSource.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');

    private static int LineOf(string needle) => Array.FindIndex(Lines(), l => l.Contains(needle, StringComparison.Ordinal)) + 1;

    private static int ColumnOf(string needle) => Lines().First(l => l.Contains(needle, StringComparison.Ordinal)).IndexOf("Banned()", StringComparison.Ordinal) + 1;

    private static string Result(string rule, string? level, string? uri, int line, int column, bool suppressed = false)
    {
        var sb = new StringBuilder("{");
        sb.Append($"\"ruleId\":\"{rule}\"");
        if (level is not null)
        {
            sb.Append($",\"level\":\"{level}\"");
        }

        if (suppressed)
        {
            sb.Append(",\"suppressions\":[{\"kind\":\"inSource\"}]");
        }

        if (uri is not null)
        {
            sb.Append($",\"locations\":[{{\"physicalLocation\":{{\"artifactLocation\":{{\"uri\":\"{uri.Replace("\\", "/", StringComparison.Ordinal)}\"}},\"region\":{{\"startLine\":{line},\"startColumn\":{column}}}}}}}]");
        }

        return sb.Append('}').ToString();
    }

    private string WriteSarif(string relativePath, params string[] results)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"runs\":[{\"results\":[" + string.Join(",", results) + "]}]}");
        return path;
    }

    private static string Hit(string line, string uri = "Sample/Repo.cs", string rule = "RS0030", string level = "warning") =>
        Result(rule, level, uri, LineOf(line), ColumnOf(line));

    private IReadOnlyList<BannedCallSite> Scan(params string[] sarif) => new RoslynBannedCallScanner().Scan(_root, sarif);

    [Theory]
    [InlineData("10.0.1", "9.0.11")]
    [InlineData("10.0.12", "10.0.9")]
    [InlineData("10.0.0", "10.0.0-rc.2.25502.107")]
    public void Shared_framework_folders_are_ranked_by_version_not_by_text(string newer, string older)
    {
        RoslynBannedCallScanner.FrameworkVersion(Path.Combine("shared", newer))
            .Should().BeGreaterThanOrEqualTo(RoslynBannedCallScanner.FrameworkVersion(Path.Combine("shared", older)));
        RoslynBannedCallScanner.FrameworkVersion(Path.Combine("shared", "9.0.11"))
            .Should().BeLessThan(RoslynBannedCallScanner.FrameworkVersion(Path.Combine("shared", "10.0.1")));
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Only_unsuppressed_counted_RS0030_results_with_a_location_are_returned()
    {
        var sarif = WriteSarif(
            "Sample.sarif",
            Hit("return"),
            Hit("Prop", rule: "CA1000"),
            Result("RS0030", "warning", "Sample/Repo.cs", LineOf("_field"), ColumnOf("_field"), suppressed: true),
            Result("RS0030", "note", "Sample/Repo.cs", LineOf("_field"), ColumnOf("_field")),
            Result("RS0030", "warning", null, 0, 0));

        Scan(sarif).Should().Equal(new BannedCallSite("Sample", "M:Demo.Repo.Method"));
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Missing_level_defaults_to_warning_and_error_level_is_counted()
    {
        var sarif = WriteSarif(
            "Sample.sarif",
            Result("RS0030", null, "Sample/Repo.cs", LineOf("Prop"), ColumnOf("Prop")),
            Hit("return", level: "error"));

        Scan(sarif).Select(s => s.MemberDocId).Should().Equal("M:Demo.Repo.Method", "P:Demo.Repo.Prop");
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Members_resolve_to_method_property_and_field_ids_without_duplicates_in_ordinal_order()
    {
        var sarif = WriteSarif("Sample.sarif", Hit("Prop"), Hit("return"), Hit("return"), Hit("_field"));

        Scan(sarif).Should().Equal(
            new BannedCallSite("Sample", "F:Demo.Repo._field"),
            new BannedCallSite("Sample", "M:Demo.Repo.Method"),
            new BannedCallSite("Sample", "P:Demo.Repo.Prop"));
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Sites_are_ordered_by_project_then_member_regardless_of_file_order()
    {
        AddProject("Alpha");
        AddProject("Zeta");
        var zeta = WriteSarif("a/Zeta.sarif", Hit("Prop", "Zeta/Repo.cs"), Hit("return", "Zeta/Repo.cs"));
        var alpha = WriteSarif("z/Alpha.sarif", Hit("Prop", "Alpha/Repo.cs"));

        Scan(zeta, alpha).Should().Equal(
            new BannedCallSite("Alpha", "P:Demo.Repo.Prop"),
            new BannedCallSite("Zeta", "M:Demo.Repo.Method"),
            new BannedCallSite("Zeta", "P:Demo.Repo.Prop"));
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Absolute_file_uri_and_repo_relative_uri_resolve_to_the_same_member()
    {
        var absolute = new Uri(Path.Combine(_root, "Sample", "Repo.cs")).AbsoluteUri;
        var viaAbsolute = WriteSarif("Sample.sarif", Hit("return", absolute));
        var viaRelative = WriteSarif("Other/Sample.sarif", Hit("return"));

        Scan(viaAbsolute).Should().Equal(new BannedCallSite("Sample", "M:Demo.Repo.Method"));
        Scan(viaRelative).Should().Equal(new BannedCallSite("Sample", "M:Demo.Repo.Method"));
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Files_in_nested_folders_and_generated_global_usings_are_part_of_the_project()
    {
        var nested = Path.Combine(_root, "Sample", "sub");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "Nested.cs"), "namespace Demo;\npublic class Nested\n{\n    public void Run() { }\n}\n");
        var objDir = Path.Combine(_root, "Sample", "obj", "Debug");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "Sample.GlobalUsings.g.cs"), "global using System;\n");

        var sarif = WriteSarif("Sample.sarif", Result("RS0030", "warning", "Sample/sub/Nested.cs", 4, 5));
        Scan(sarif).Should().Equal(new BannedCallSite("Sample", "M:Demo.Nested.Run"));

        var usings = WriteSarif("U/Sample.sarif", Result("RS0030", "warning", "Sample/obj/Debug/Sample.GlobalUsings.g.cs", 1, 1));
        var act = () => Scan(usings);
        act.Should().Throw<InvalidOperationException>().WithMessage("*not inside a member*");
    }

    [Theory]
    [Trait("scenario", "US2-AS43")]
    [InlineData("Sample/obj/Extra.cs")]
    [InlineData("Sample/bin/Extra.cs")]
    [InlineData("Sample/sub/obj/Extra.cs")]
    [InlineData("Sample/sub/bin/Extra.cs")]
    public void Sources_under_obj_or_bin_are_not_part_of_the_project(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "namespace Demo;\npublic class Extra\n{\n    public void Run() { }\n}\n");
        var sarif = WriteSarif("Sample.sarif", Result("RS0030", "warning", relative, 4, 5));

        var act = () => Scan(sarif);

        act.Should().Throw<InvalidOperationException>().WithMessage("*is not part of project Sample*");
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Line_zero_and_line_past_the_end_are_rejected_but_the_edge_lines_are_addressable()
    {
        var count = Lines().Length;

        foreach (var line in new[] { 0, count + 1 })
        {
            var sarif = WriteSarif("Sample.sarif", Result("RS0030", "warning", "Sample/Repo.cs", line, 1));
            var act = () => Scan(sarif);
            act.Should().Throw<InvalidOperationException>().WithMessage("*is outside the file*");
        }

        foreach (var line in new[] { 1, count })
        {
            var sarif = WriteSarif("Sample.sarif", Result("RS0030", "warning", "Sample/Repo.cs", line, 1));
            var act = () => Scan(sarif);
            act.Should().Throw<InvalidOperationException>().WithMessage("*not inside a member*");
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void A_file_outside_any_project_is_rejected()
    {
        var orphan = Directory.CreateTempSubdirectory("qg-orphan-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(orphan, "x.cs"), "class X { }");
            var sarif = WriteSarif("Sample.sarif", Result("RS0030", "warning", new Uri(Path.Combine(orphan, "x.cs")).AbsoluteUri, 1, 1));

            var act = () => Scan(sarif);

            act.Should().Throw<InvalidOperationException>().WithMessage("no .csproj found above*");
        }
        finally
        {
            Directory.Delete(orphan, recursive: true);
        }
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void A_location_outside_any_member_is_rejected()
    {
        var sarif = WriteSarif("Sample.sarif", Result("RS0030", "warning", "Sample/Repo.cs", 1, 1));

        var act = () => Scan(sarif);

        act.Should().Throw<InvalidOperationException>().WithMessage("*is not inside a member that can be suppressed*");
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Sarif_directory_input_is_searched_recursively_for_sarif_files_only()
    {
        var dir = Path.Combine(_root, "reports");
        WriteSarif("reports/Sample.sarif", Hit("return"));
        AddProject("Deep");
        WriteSarif("reports/nested/Deep.sarif", Hit("Prop", "Deep/Repo.cs"));
        File.WriteAllText(Path.Combine(dir, "notes.json"), "not json");

        Scan(dir).Should().Equal(
            new BannedCallSite("Deep", "P:Demo.Repo.Prop"),
            new BannedCallSite("Sample", "M:Demo.Repo.Method"));
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Sarif_glob_matches_only_the_given_directory_and_explicit_files_are_used_as_is()
    {
        WriteSarif("g/Sample.sarif", Hit("return"));
        AddProject("Deep");
        WriteSarif("g/nested/Deep.sarif", Hit("Prop", "Deep/Repo.cs"));
        File.WriteAllText(Path.Combine(_root, "g", "other.txt"), "x");

        Scan(Path.Combine(_root, "g", "*.sarif")).Should().Equal(new BannedCallSite("Sample", "M:Demo.Repo.Method"));
        Scan(Path.Combine(_root, "g", "nested", "Deep.sarif")).Should().Equal(new BannedCallSite("Deep", "P:Demo.Repo.Prop"));
    }

    [Theory]
    [Trait("scenario", "US2-AS43")]
    [InlineData("missing.sarif")]
    [InlineData("nodir/*.sarif")]
    [InlineData("Sample/absent.sarif")]
    public void Missing_sarif_input_throws_FileNotFound(string relative)
    {
        var act = () => Scan(Path.Combine(_root, relative));

        act.Should().Throw<FileNotFoundException>().WithMessage("SARIF input not found:*");
    }

    private static void EmitDll(string path, string code)
    {
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(path),
            [CSharpSyntaxTree.ParseText(code)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        compilation.Emit(stream).Success.Should().BeTrue();
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Platform_and_project_bin_references_resolve_parameter_types_in_member_ids()
    {
        var bin = Path.Combine(_root, "Sample", "bin", "Debug");
        EmitDll(Path.Combine(bin, "Extra.dll"), "namespace Ext { public class Widget { } }");
        EmitDll(Path.Combine(bin, "Sample.dll"), "namespace Own { public class Thing { } }");
        File.WriteAllText(
            Path.Combine(_root, "Sample", "Typed.cs"),
            "using System.Collections.Generic;\nusing Own;\nnamespace Demo;\npublic class Typed\n{\n    public int A(List<int> l) => 1;\n    public int B(Ext.Widget w) => 1;\n    public int C(Thing t) => 1;\n}\n");
        var sarif = WriteSarif(
            "Sample.sarif",
            Result("RS0030", "warning", "Sample/Typed.cs", 6, 5),
            Result("RS0030", "warning", "Sample/Typed.cs", 7, 5));

        Scan(sarif).Select(s => s.MemberDocId).Should().Equal(
            "M:Demo.Typed.A(System.Collections.Generic.List{System.Int32})",
            "M:Demo.Typed.B(Ext.Widget)");
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Unresolved_parameter_type_is_an_error_not_an_unqualified_member_id()
    {
        File.WriteAllText(
            Path.Combine(_root, "Sample", "Typed.cs"),
            "using Missing.Lib;\nnamespace Demo;\npublic class Typed\n{\n    public int C(IThing t) => 1;\n}\n");
        var sarif = WriteSarif("Sample.sarif", Result("RS0030", "warning", "Sample/Typed.cs", 5, 5));

        var act = () => Scan(sarif);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'IThing'*does not resolve*");
    }

    [Fact]
    [Trait("scenario", "US2-AS43")]
    public void Package_references_from_project_assets_resolve_to_fully_qualified_member_ids()
    {
        var packages = Path.Combine(_root, "packages");
        var dll = Path.Combine(packages, "dep", "1.0.0", "lib", "net8.0", "Dep.dll");
        EmitDll(dll, "namespace Microsoft.Extensions.DependencyInjection { public interface IServiceCollection { } }");
        var assets = new
        {
            targets = new Dictionary<string, object> { ["net8.0"] = new Dictionary<string, object> { ["Dep/1.0.0"] = new { type = "package", compile = new Dictionary<string, object> { ["lib/net8.0/Dep.dll"] = new { } } } } },
            libraries = new Dictionary<string, object> { ["Dep/1.0.0"] = new { type = "package", path = "dep/1.0.0" } },
            packageFolders = new Dictionary<string, object> { [packages] = new { } }
        };
        Directory.CreateDirectory(Path.Combine(_root, "Sample", "obj"));
        File.WriteAllText(Path.Combine(_root, "Sample", "obj", "project.assets.json"), System.Text.Json.JsonSerializer.Serialize(assets));
        File.WriteAllText(
            Path.Combine(_root, "Sample", "Root.cs"),
            "using Microsoft.Extensions.DependencyInjection;\nnamespace Demo;\npublic static class Root\n{\n    public static int Register(IServiceCollection services) => 1;\n}\n");
        var sarif = WriteSarif("Sample.sarif", Result("RS0030", "warning", "Sample/Root.cs", 5, 5));

        Scan(sarif).Select(s => s.MemberDocId).Should().Equal("M:Demo.Root.Register(Microsoft.Extensions.DependencyInjection.IServiceCollection)");
    }
}
