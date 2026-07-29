using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using triaxis.WebForms.MSBuildTasks;
using triaxis.WebForms.SourceGenerator.Emit;

namespace triaxis.WebForms.SourceGenerator.Tests;

public class PreservationFileTests
{
    [Theory]
    // Ground truth: filenames the real aspnet_compiler wrote for these handlers.
    [InlineData("/WebServices/Authenticate.asmx", "authenticate.asmx.f0e9dea3.compiled")]
    [InlineData("/Forms/getExternalIntegrationHelp.ashx", "getexternalintegrationhelp.ashx.ba00be51.compiled")]
    [InlineData("/MedHubServices/StaffingService.svc", "staffingservice.svc.25deb41f.compiled")]
    public void Name_matches_aspnet_compiler(string virtualPath, string expected)
        => Assert.Equal(expected, PreservationFile.Name(virtualPath));

    [Theory]
    [InlineData("<%@ WebService Class=\"Watchtower.ServerInterface.WebServiceCalls.Authenticate\" %>", "Class", "Watchtower.ServerInterface.WebServiceCalls.Authenticate")]
    [InlineData("<%@ WebHandler CodeBehind=\"x.ashx.cs\" Class=\"MomentumWeb.ExternalIntegrationHelpHandler\" %>", "Class", "MomentumWeb.ExternalIntegrationHelpHandler")]
    [InlineData("<%@ ServiceHost Debug=\"false\" Service=\"Momentum.MedHubServices.StaffingService\" Codebehind=\"~/x.cs\" %>", "Service", "Momentum.MedHubServices.StaffingService")]
    [InlineData("<%@ ServiceHost Service=\"A.B\" %>", "Factory", null)]
    public void ExtractAttribute_reads_directive(string markup, string attribute, string? expected)
        => Assert.Equal(expected, PreservationFile.ExtractAttribute(markup, attribute));

    [Fact]
    public void PreserveType_emits_resultType2_for_handler()
    {
        string xml = PreservationFile.PreserveType(PreservationFile.CompiledType, "/WebServices/Authenticate.asmx",
            "Watchtower.ServerInterface", "Watchtower.ServerInterface.WebServiceCalls.Authenticate");

        Assert.Contains("resultType=\"2\"", xml);
        Assert.Contains("virtualPath=\"/WebServices/Authenticate.asmx\"", xml);
        Assert.Contains("assembly=\"Watchtower.ServerInterface\"", xml);
        Assert.Contains("type=\"Watchtower.ServerInterface.WebServiceCalls.Authenticate\"", xml);
    }

    [Theory]
    [InlineData("/App_Themes/Momentum_Theme/site.css", "Momentum_Theme")]
    [InlineData("/App_Themes/Momentum_Theme/controls/grid.skin", "Momentum_Theme")]
    // Directly in App_Themes\, so part of no theme.
    [InlineData("/App_Themes/orphan.css", null)]
    [InlineData("/Content/site.css", null)]
    public void ThemeName_reads_the_theme_folder(string virtualPath, string? expected)
        => Assert.Equal(expected, PreservationFile.ThemeName(virtualPath));

    [Theory]
    [InlineData("Momentum_Theme", "Momentum_Theme")]
    [InlineData("My-Theme", "My_Theme")]
    [InlineData("blue theme.v2", "blue_theme_v2")]
    // Identifiers can't lead with a digit.
    [InlineData("2fast", "_2fast")]
    // A keyword is a valid metadata name; only the declaration escapes it.
    [InlineData("default", "default")]
    public void Identifier_mangles_a_name_into_an_identifier(string folder, string expected)
    {
        Assert.Equal(expected, PreservationFile.Identifier(folder));
        // The sidecar's type must name the type the generator declares, so both
        // assemblies have to derive it identically.
        Assert.Equal(GeneratedNaming.Identifier(folder), PreservationFile.Identifier(folder));
    }

    [Fact]
    public void PreserveType_emits_resultType3_for_theme()
    {
        // Ground truth: the Theme_Momentum_Theme.compiled aspnet_compiler wrote.
        string xml = PreservationFile.PreserveType(PreservationFile.CompiledTemplateType,
            "/App_Themes/Momentum_Theme/", "MomentumWeb.Compiled", "ASP.Momentum_Theme");

        Assert.Contains("resultType=\"3\"", xml);
        // Trailing slash: the result is a directory, not a file.
        Assert.Contains("virtualPath=\"/App_Themes/Momentum_Theme/\"", xml);
        Assert.Contains("type=\"ASP.Momentum_Theme\"", xml);
    }

    [Fact]
    public void Svc_custom_string_is_path_factory_service_defining_assembly()
    {
        string custom = PreservationFile.ServiceCustomString(
            "/MedHubServices/StaffingService.svc", factory: "",
            service: "Momentum.MedHubServices.StaffingService", assembly: "MomentumWeb");

        // Empty factory slot (defaults to ServiceHostFactory) + the single
        // assembly the WCF host reads to resolve the service type.
        Assert.Equal(
            "~/MedHubServices/StaffingService.svc||Momentum.MedHubServices.StaffingService|MomentumWeb",
            custom);

        string xml = PreservationFile.PreserveCustomString("/MedHubServices/StaffingService.svc", custom);
        Assert.Contains("resultType=\"5\"", xml);
        Assert.Contains("customString=\"~/MedHubServices/StaffingService.svc||", xml);
    }

    [Fact]
    public void ResolveAssemblies_binds_class_to_defining_assembly()
    {
        using var dir = new TempDir();
        string refAsm = dir.Emit("Some.Ref.Asm",
            "namespace Watchtower.ServerInterface.WebServiceCalls { public class Authenticate {} }");
        string unrelated = dir.Emit("Other", "namespace Whatever { public class Thing {} }");

        Dictionary<string, string> resolved = PreservationFile.ResolveAssemblies(
            new HashSet<string>(StringComparer.Ordinal) { "Watchtower.ServerInterface.WebServiceCalls.Authenticate" },
            new[] { unrelated, refAsm });

        Assert.Equal("Some.Ref.Asm", Assert.Contains("Watchtower.ServerInterface.WebServiceCalls.Authenticate", resolved));
    }

    [Fact]
    public void ResolveAssemblies_binds_nested_class()
    {
        using var dir = new TempDir();
        string asm = dir.Emit("Nested.Asm", "namespace N { public class Outer { public class Inner {} } }");

        Dictionary<string, string> resolved = PreservationFile.ResolveAssemblies(
            new HashSet<string>(StringComparer.Ordinal) { "N.Outer+Inner" }, new[] { asm });

        Assert.Equal("Nested.Asm", Assert.Contains("N.Outer+Inner", resolved));
    }

    [Fact]
    public void ResolveAssemblies_omits_unfound_types()
        => Assert.Empty(PreservationFile.ResolveAssemblies(
            new HashSet<string>(StringComparer.Ordinal) { "Nope.Missing" }, Array.Empty<string>()));

    [Theory]
    [InlineData("Ns.Type", "Ns.Type", null)]
    [InlineData("Ns.Type, Some.Assembly", "Ns.Type", "Some.Assembly")]
    [InlineData("  Ns.Type ,  Some.Assembly, Version=1.0.0.0 ", "Ns.Type", "Some.Assembly, Version=1.0.0.0")]
    public void SplitTypeName_separates_assembly_qualifier(string value, string type, string? assembly)
    {
        (string Type, string? Assembly) split = PreservationFile.SplitTypeName(value);
        Assert.Equal(type, split.Type);
        Assert.Equal(assembly, split.Assembly);
    }

    [Fact]
    public void ResolveAssemblies_skips_non_managed_files()
    {
        using var dir = new TempDir();
        string asm = dir.Emit("Some.Ref.Asm", "namespace N { public class C {} }");
        string native = Path.Combine(dir.Path, "native.dll");
        File.WriteAllText(native, "not a PE file");

        Dictionary<string, string> resolved = PreservationFile.ResolveAssemblies(
            new HashSet<string>(StringComparer.Ordinal) { "N.C" }, new[] { native, asm });

        Assert.Equal("Some.Ref.Asm", Assert.Contains("N.C", resolved));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "twf-" + Guid.NewGuid().ToString("n"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string Emit(string assemblyName, string source)
        {
            string path = System.IO.Path.Combine(Path, assemblyName + ".dll");
            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source) },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var fs = File.Create(path);
            EmitResult result = compilation.Emit(fs);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
