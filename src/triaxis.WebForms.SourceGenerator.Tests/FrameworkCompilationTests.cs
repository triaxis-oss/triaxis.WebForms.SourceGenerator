using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using triaxis.WebForms.SourceGenerator.Emit;
using triaxis.WebForms.SourceGenerator.Model;
using triaxis.WebForms.SourceGenerator.Parsing;

namespace triaxis.WebForms.SourceGenerator.Tests;

/// <summary>
/// Compiles generated frames against real net48 metadata rather than the
/// hand-written System.Web stubs the other tests use. Stubs only prove the
/// generator emits the text we expected; these prove the framework accepts it.
/// <c>Async="true"</c> shipped broken (CS0535: Page implements
/// IHttpAsyncHandler explicitly, so re-declaring the interface leaves both
/// members unimplemented) precisely because nothing here existed.
/// </summary>
public class FrameworkCompilationTests
{
    [Fact]
    public void Async_page_frame_compiles_against_System_Web()
    {
        string frame = EmitFrame(
            "<%@ Page Async=\"true\" AsyncTimeout=\"45\" Inherits=\"System.Web.UI.Page\" %>\r\n");

        AssertCompiles(frame);
        Assert.Contains("AsyncTimeout = global::System.TimeSpan.FromSeconds(45D);", frame);
    }

    [Fact]
    public void Synchronous_page_frame_compiles_against_System_Web()
    {
        AssertCompiles(EmitFrame("<%@ Page Inherits=\"System.Web.UI.Page\" %>\r\n"));
    }

    private static string EmitFrame(string markup)
    {
        MarkupDirective directive = MarkupParserDriver
            .Parse("Default.aspx", new StringReader(markup)).Directive!;
        return PageFrameEmitter.Emit(directive, "/Default.aspx");
    }

    private static void AssertCompiles(string frame)
    {
        // The frame references ASP._global_asax (emitted separately) and calls
        // InitializeCulture, which is protected on Page — supply the first and
        // let the second compile from inside the generated subclass.
        const string companions =
            "namespace ASP { public class _global_asax : global::System.Web.HttpApplication { } }\n";

        CSharpCompilation compilation = CSharpCompilation.Create(
            "frameworkprobe",
            new[] { CSharpSyntaxTree.ParseText(frame), CSharpSyntaxTree.ParseText(companions) },
            ReferenceAssemblies(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        string[] errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();

        Assert.Empty(errors);
    }

    private static IEnumerable<MetadataReference> ReferenceAssemblies()
    {
        string directory = typeof(FrameworkCompilationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ReferenceAssemblies")
            .Value!;

        foreach (string name in new[] { "mscorlib", "System", "System.Core", "System.Web", "System.Configuration" })
        {
            yield return MetadataReference.CreateFromFile(Path.Combine(directory, name + ".dll"));
        }
    }
}
