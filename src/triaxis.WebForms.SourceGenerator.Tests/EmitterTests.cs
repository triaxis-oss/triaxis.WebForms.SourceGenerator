using System.IO;
using triaxis.WebForms.SourceGenerator.Emit;
using triaxis.WebForms.SourceGenerator.Model;
using triaxis.WebForms.SourceGenerator.Parsing;

namespace triaxis.WebForms.SourceGenerator.Tests;

public class EmitterTests
{
    private const string SamplePage =
        "<%@ Page Language=\"C#\" AutoEventWireup=\"false\" Inherits=\"Sample.Forms_frmHome\" Title=\"Hello\" %>\r\n" +
        "<!DOCTYPE html>\r\n" +
        "<html>\r\n" +
        "<head runat=\"server\"><title>T</title></head>\r\n" +
        "<body>\r\n" +
        "<form id=\"form1\" runat=\"server\"><asp:Label runat=\"server\" ID=\"lbl\" Text=\"hi\" /></form>\r\n" +
        "</body>\r\n" +
        "</html>\r\n";

    private static ParsedMarkup ParseSample()
    {
        return MarkupParserDriver.Parse("Default.aspx", new StringReader(SamplePage));
    }

    [Fact]
    public void Directive_attributes_are_captured()
    {
        MarkupDirective? directive = ParseSample().Directive;

        Assert.NotNull(directive);
        Assert.Equal(MarkupKind.Page, directive!.Kind);
        Assert.Equal("Sample.Forms_frmHome", directive.Inherits);
        Assert.Equal("Hello", directive.Title);
        Assert.False(directive.AutoEventWireup);
    }

    [Fact]
    public void Frame_reproduces_the_oracle_structural_members()
    {
        MarkupDirective directive = ParseSample().Directive!;

        string frame = PageFrameEmitter.Emit(directive, "/Default.aspx");

        Assert.Contains("public class default_aspx : global::Sample.Forms_frmHome", frame);
        Assert.Contains("global::System.Web.SessionState.IRequiresSessionState", frame);
        Assert.Contains("AppRelativeVirtualPath = \"~/Default.aspx\";", frame);
        Assert.Contains("__fileDependencies = GetWrappedFileDependencies(new string[] { \"~/Default.aspx\" });", frame);
        Assert.Contains("protected override void FrameworkInitialize()", frame);
        Assert.Contains("public override void ProcessRequest(global::System.Web.HttpContext context)", frame);
        // AutoEventWireup="false" → the SupportAutoEvents override is emitted.
        Assert.Contains("protected override bool SupportAutoEvents => false;", frame);
    }

    [Fact]
    public void Fold_classifies_server_controls_and_literals()
    {
        ServerControlNode root = MarkupTreeFolder.Fold("Default.aspx", SamplePage, serverPrefixes: null, out IReadOnlyList<string> errors);

        Assert.Empty(errors);
        // <html> is plain markup → literal; <head runat=server> and <form> are controls.
        Assert.Contains(root.Children, n => n is LiteralNode lit && lit.Text.Contains("<!DOCTYPE html>"));
        Assert.Contains(root.Children, n => n is ServerControlNode c && c.TagName == "head");
        Assert.Contains(root.Children, n => n is ServerControlNode c && c.Id == "form1");
    }

    [Fact]
    public void Full_emit_builds_the_control_tree()
    {
        ParsedMarkup parsed = ParseSample();
        ServerControlNode root = MarkupTreeFolder.Fold("Default.aspx", SamplePage, serverPrefixes: null, out _);

        // Stand-in binder: assign every attribute as a string property (the
        // real generator binder resolves property types via the compilation).
        AttributeBinding Binder(string type, string name, string value, string? container) =>
            AttributeBinding.Property(name, "\"" + value + "\"");
        ChildPlacement Classify(string parent, string child, bool isServer) => ChildPlacement.ParsedSubObject();
        bool ControlContainer(string parent) => true;
        bool Themeable(string type) => false;
        bool Control(string type) => false;

        string UcBindingType(string t) => "System.Web.UI.UserControl";
        string source = PageEmitter.Emit(parsed.Directive!, "/Default.aspx", root, new ControlTypeResolver(), SamplePage, Binder, Classify, ControlContainer, Themeable, Control, UcBindingType, new System.Collections.Generic.Dictionary<string, string>(), out IReadOnlyList<string> diagnostics, out IReadOnlyList<ControlTreeEmitter.FieldBindingMismatch> mismatches);

        Assert.Empty(diagnostics);
        Assert.Empty(mismatches);
        Assert.Contains("__BuildControlform1()", source);
        Assert.Contains("__BuildControllbl()", source);
        Assert.Contains("new global::System.Web.UI.WebControls.Label()", source);
        Assert.Contains("__ctrl.ID = \"lbl\";", source);
        Assert.Contains("__ctrl.Text = \"hi\";", source);
        // The form is built and attached to the page in __BuildControlTree.
        Assert.Contains("AddParsedSubObject(__BuildControlform1());", source);
    }

    [Theory]
    [InlineData("asp", "Label", "global::System.Web.UI.WebControls.Label")]
    [InlineData(null, "head", "global::System.Web.UI.HtmlControls.HtmlHead")]
    [InlineData(null, "form", "global::System.Web.UI.HtmlControls.HtmlForm")]
    public void Resolver_maps_builtin_prefixes(string? prefix, string tag, string expectedType)
    {
        ResolvedControl? resolved = new ControlTypeResolver().Resolve(prefix, tag);

        Assert.NotNull(resolved);
        Assert.Equal(expectedType, resolved!.Value.TypeName);
    }

    [Fact]
    public void Resolver_falls_back_to_generic_html_control()
    {
        ResolvedControl? resolved = new ControlTypeResolver().Resolve(null, "section");

        Assert.NotNull(resolved);
        Assert.Equal("global::System.Web.UI.HtmlControls.HtmlGenericControl", resolved!.Value.TypeName);
        Assert.Equal("\"section\"", resolved.Value.ConstructorArgument);
    }

    [Fact]
    public void Resolver_returns_null_for_unknown_prefix()
    {
        ResolvedControl? resolved = new ControlTypeResolver().Resolve("telerik", "RadGrid");

        Assert.Null(resolved);
    }

    [Fact]
    public void Registered_prefix_namespace_resolves()
    {
        var resolver = new ControlTypeResolver(new Dictionary<string, string> { ["telerik"] = "Telerik.Web.UI" });

        ResolvedControl? resolved = resolver.Resolve("telerik", "RadGrid");

        Assert.NotNull(resolved);
        Assert.Equal("global::Telerik.Web.UI.RadGrid", resolved!.Value.TypeName);
    }
}
