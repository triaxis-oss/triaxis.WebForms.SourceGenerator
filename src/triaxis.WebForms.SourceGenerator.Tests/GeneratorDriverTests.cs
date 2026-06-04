using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace triaxis.WebForms.SourceGenerator.Tests;

public class GeneratorDriverTests
{
    [Fact]
    public void Generator_emits_markup_manifest_for_additional_files()
    {
        var compilation = CSharpCompilation.Create("test");
        var additionalTexts = new AdditionalText[]
        {
            new InMemoryAdditionalText("/proj/Forms/frmHome.aspx", "<%@ Page %>"),
            new InMemoryAdditionalText("/proj/Controls/ctlThing.ascx", "<%@ Control %>"),
            new InMemoryAdditionalText("/proj/readme.txt", "ignored"),
        };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);

        driver = driver.RunGenerators(compilation);
        GeneratorDriverRunResult result = driver.GetRunResult();

        SyntaxTree manifest = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("MarkupManifest.g.cs"));
        string text = manifest.GetText().ToString();

        // Only the two markup files count; the .txt is filtered out.
        Assert.Contains("internal const int Count = 2;", text);
        Assert.Contains("\"/Controls/ctlThing.ascx\"", text);
        Assert.Contains("\"/Forms/frmHome.aspx\"", text);
        Assert.DoesNotContain("readme.txt", text);
        Assert.Empty(result.Results.Single().Diagnostics);
    }

    [Fact]
    public void Generator_emits_full_page_type_when_enabled()
    {
        // Stub the control types so the compilation-backed resolver finds them;
        // without these the generator would fall back to verbatim literals.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace System.Web.UI.WebControls { public class Label { public string Text { get; set; } } }\n";
        var compilation = CSharpCompilation.Create("test", new[] { CSharpSyntaxTree.ParseText(stubs) });
        const string markup =
            "<%@ Page Inherits=\"Foo.Bar\" Title=\"X\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><asp:Label runat=\"server\" ID=\"lbl\" Text=\"hi\" /></form>\r\n";
        var additionalTexts = new AdditionalText[] { new InMemoryAdditionalText("/proj/Default.aspx", markup) };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);

        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();

        SyntaxTree page = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("default_aspx.g.cs"));
        string text = page.GetText().ToString();

        Assert.Contains("public class default_aspx : global::Foo.Bar", text);
        Assert.Contains("__BuildControllbl()", text);
        Assert.Contains("new global::System.Web.UI.WebControls.Label()", text);
        // Text resolves to the stub's string property → typed assignment, not SetAttribute.
        Assert.Contains("__ctrl.Text = \"hi\";", text);
    }

    [Fact]
    public void Generator_emits_typed_binds_theming_and_page_defaults()
    {
        // Minimal control surface: a themeable Widget with a flags enum, a Color
        // property and a complex Font sub-object, hosted in an HtmlForm container.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } public void ApplyStyleSheetSkin(Page p) { } }\n" +
            "  public class Page : Control { public string Theme { get; set; } public bool EnableEventValidation { get; set; } public string Title { get; set; } public void InitializeCulture() { } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace System.Web.UI.WebControls { public struct FontUnit { } }\n" +
            "namespace System.Drawing { public struct Color { public static Color Red { get; } public static Color FromArgb(int r, int g, int b) { return default; } } }\n" +
            "namespace Demo {\n" +
            "  [System.Flags] public enum Behaviors { None = 0, Move = 1, Resize = 2 }\n" +
            "  public class FontInfo { public bool Bold { get; set; } public string[] Names { get; set; } public System.Web.UI.WebControls.FontUnit Size { get; set; } }\n" +
            "  public class Widget : System.Web.UI.Control { public Behaviors Behaviors { get; set; } public System.Drawing.Color ForeColor { get; set; } public FontInfo Font { get; } } }\n";
        var compilation = CSharpCompilation.Create("test", new[] { CSharpSyntaxTree.ParseText(stubs) });

        const string markup =
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><my:Widget runat=\"server\" ID=\"w\" Behaviors=\"Move, Resize\" " +
            "ForeColor=\"Red\" Font-Bold=\"true\" Font-Names=\"Arial\" Font-Size=\"8pt\" /></form>\r\n";
        const string webConfig =
            "<configuration><system.web><pages theme=\"MyTheme\" enableEventValidation=\"false\">" +
            "<controls><add tagPrefix=\"my\" namespace=\"Demo\" /></controls></pages></system.web></configuration>";
        var additionalTexts = new AdditionalText[]
        {
            new InMemoryAdditionalText("/proj/Default.aspx", markup),
            new InMemoryAdditionalText("/proj/web.config", webConfig),
        };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);

        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();
        string text = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("default_aspx.g.cs")).GetText().ToString();

        // Page defaults from web.config <pages>.
        Assert.Contains("__ctrl.Theme = \"MyTheme\";", text);
        Assert.Contains("__ctrl.EnableEventValidation = false;", text);
        // Themeable control picks up the skin.
        Assert.Contains("ApplyStyleSheetSkin", text);
        // Flags enum OR-combined; color via translator; font sub-properties.
        Assert.Contains("__ctrl.Behaviors = global::Demo.Behaviors.Move | global::Demo.Behaviors.Resize;", text);
        Assert.Contains("__ctrl.ForeColor = global::System.Drawing.Color.Red;", text);
        Assert.Contains("__ctrl.Font.Bold = true;", text);
        Assert.Contains("__ctrl.Font.Names = new string[] { \"Arial\" };", text);
        Assert.Contains("__ctrl.Font.Size = new global::System.Web.UI.WebControls.FontUnit(new global::System.Web.UI.WebControls.Unit(8.0, global::System.Web.UI.WebControls.UnitType.Point));", text);
        // None of the above should fall back to SetAttribute.
        Assert.DoesNotContain("SetAttribute", text);
    }

    [Fact]
    public void Content_mixing_code_and_controls_renders_through_a_delegate()
    {
        // A region with literals, a <%= %> expression and a child control must
        // become a render delegate: the control is still added (so it occupies
        // parameterContainer.Controls), but markup/code render inline.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace System.Web.UI.WebControls { public class Label { public string Text { get; set; } } }\n";
        var compilation = CSharpCompilation.Create("test", new[] { CSharpSyntaxTree.ParseText(stubs) });
        const string markup =
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\">hello <%= Now() %> <asp:Label runat=\"server\" ID=\"lbl\" /></form>\r\n";
        var additionalTexts = new AdditionalText[] { new InMemoryAdditionalText("/proj/Default.aspx", markup) };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);

        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();
        string text = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("default_aspx.g.cs")).GetText().ToString();

        Assert.Contains("SetRenderMethodDelegate", text);
        Assert.Contains("AddParsedSubObject(__BuildControllbl());", text);
        Assert.Contains("__w.Write(Now());", text);
        Assert.Contains("parameterContainer.Controls[0].RenderControl(__w);", text);
        // The in-region literal is written inline, not built as a LiteralControl.
        Assert.Contains("__w.Write(\"hello \");", text);
    }

    [Fact]
    public void Input_resolves_by_type_and_table_cell_takes_its_tag_name()
    {
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); } public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.HtmlControls {\n" +
            "  public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } }\n" +
            "  public class HtmlInputText : System.Web.UI.Control { }\n" +
            "  public class HtmlInputHidden : System.Web.UI.Control { }\n" +
            "  public class HtmlTableCell : System.Web.UI.Control { public HtmlTableCell() { } public HtmlTableCell(string tag) { } } }\n";
        var compilation = CSharpCompilation.Create("test", new[] { CSharpSyntaxTree.ParseText(stubs) });
        const string markup =
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><input type=\"hidden\" runat=\"server\" id=\"h\" /><td runat=\"server\" id=\"c\"></td></form>\r\n";
        var additionalTexts = new AdditionalText[] { new InMemoryAdditionalText("/proj/Default.aspx", markup) };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);

        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();
        string text = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("default_aspx.g.cs")).GetText().ToString();

        // <input type="hidden"> is an HtmlInputHidden, not the default HtmlInputText.
        Assert.Contains("new global::System.Web.UI.HtmlControls.HtmlInputHidden()", text);
        Assert.DoesNotContain("HtmlInputText", text);
        // <td> is built with its tag name so td/th is preserved.
        Assert.Contains("new global::System.Web.UI.HtmlControls.HtmlTableCell(\"td\")", text);
    }

    [Fact]
    public void Nested_layout_table_inside_a_server_table_stays_literal_with_runat_islands_lifted()
    {
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); } public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.WebControls { public class Label : System.Web.UI.Control { } }\n" +
            "namespace System.Web.UI.HtmlControls {\n" +
            "  public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } }\n" +
            "  public class HtmlTable : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } }\n" +
            "  public class HtmlTableRow : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } }\n" +
            "  public class HtmlTableCell : System.Web.UI.Control, System.Web.UI.IParserAccessor { public HtmlTableCell() { } public HtmlTableCell(string tag) { } public void AddParsedSubObject(object o) { } } }\n";
        var compilation = CSharpCompilation.Create("test", new[] { CSharpSyntaxTree.ParseText(stubs) });

        // A server <table> whose cell wraps a plain layout <table> that itself
        // contains a runat control: the plain table's tr/td must stay literal (its
        // </td> must not close the outer server cell) and only the runat control is
        // lifted out — the way the ASP.NET compiler folds it.
        // The second plain row is the discriminator: if the first nested </td>
        // wrongly closed the outer server cell, this row would be promoted to a
        // spurious server row/cell under the server table.
        const string markup =
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><table runat=\"server\" id=\"t\"><tr><td>\r\n" +
            "  <table><tr><td><asp:Label runat=\"server\" id=\"inner\" /></td></tr>\r\n" +
            "  <tr><td>plain</td></tr></table>\r\n" +
            "</td></tr></table></form>\r\n";
        var additionalTexts = new AdditionalText[] { new InMemoryAdditionalText("/proj/Default.aspx", markup) };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);

        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();
        string text = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("default_aspx.g.cs")).GetText().ToString();

        // Exactly one server cell is built — the nested plain <td> is not promoted.
        Assert.Equal(1, text.Split("new global::System.Web.UI.HtmlControls.HtmlTableCell(\"td\")").Length - 1);
        // The nested plain table survives as literal markup inside that cell...
        Assert.Contains("AddParsedSubObject(new global::System.Web.UI.LiteralControl(", text);
        Assert.Contains("<table>", text);
        // ...while the runat control inside it is lifted into the cell.
        Assert.Contains("new global::System.Web.UI.WebControls.Label()", text);
        Assert.Contains("__ctrl.ID = \"inner\";", text);
    }

    [Fact]
    public void Render_region_mixing_code_and_data_binding_uses_databound_literal_children()
    {
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n";
        var compilation = CSharpCompilation.Create("test", new[] { CSharpSyntaxTree.ParseText(stubs) });

        // <%= %> forces a render delegate; the <%# %> runs on either side become
        // DataBoundLiteralControl children rendered via Controls[i], the way the
        // ASP.NET compiler folds a mixed code + data-binding region.
        const string markup =
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\">a<%#Eval(\"A\")%>b<%= Bar() %>c<%#Eval(\"B\")%>d</form>\r\n";
        var additionalTexts = new AdditionalText[] { new InMemoryAdditionalText("/proj/Default.aspx", markup) };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);

        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();
        string text = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("default_aspx.g.cs")).GetText().ToString();

        // Each <%# %> run becomes a DataBoundLiteralControl (2 statics, 1 bind)...
        Assert.Equal(2, text.Split("new global::System.Web.UI.DataBoundLiteralControl(2, 1)").Length - 1);
        Assert.Contains("global::System.Convert.ToString(Eval(\"A\")", text);
        Assert.Contains("global::System.Convert.ToString(Eval(\"B\")", text);
        // ...rendered via Controls[i], with the <%= %> written inline between them.
        Assert.Contains("parameterContainer.Controls[0].RenderControl(__w);", text);
        Assert.Contains("__w.Write(Bar());", text);
        Assert.Contains("parameterContainer.Controls[1].RenderControl(__w);", text);
        Assert.DoesNotContain("data-binding inside a code-block render region", text);
    }

    [Fact]
    public void Property_element_sub_object_keeps_its_id()
    {
        const string stubs =
            "namespace System.Web.UI {\n" +
            "  public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } }\n" +
            "  public sealed class ParseChildrenAttribute : System.Attribute { public ParseChildrenAttribute(bool childrenAsProperties) { } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace Demo {\n" +
            "  public class Cal : System.Web.UI.Control { public bool ShowRowHeaders { get; set; } }\n" +
            "  [System.Web.UI.ParseChildren(true)] public class Picker : System.Web.UI.Control { public Cal Calendar { get; } } }\n";
        var compilation = CSharpCompilation.Create("test", new[] { CSharpSyntaxTree.ParseText(stubs) });

        // <Calendar> is a property element (Picker.Calendar), built in place; its
        // id="cal1" must still reach the sub-object even though it is not a child.
        const string markup =
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<%@ Register TagPrefix=\"my\" Namespace=\"Demo\" Assembly=\"test\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><my:Picker runat=\"server\" id=\"p\">" +
            "<Calendar id=\"cal1\" ShowRowHeaders=\"true\" /></my:Picker></form>\r\n";
        var additionalTexts = new AdditionalText[] { new InMemoryAdditionalText("/proj/Default.aspx", markup) };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);

        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();
        string text = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("default_aspx.g.cs")).GetText().ToString();

        // The sub-object's own attributes bind in place — including its id.
        Assert.Contains("__ctrl.Calendar.ID = \"cal1\";", text);
        Assert.Contains("__ctrl.Calendar.ShowRowHeaders = true;", text);
    }

    [Fact]
    public void Data_binding_embedded_in_a_literal_attribute_becomes_a_databound_literal()
    {
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n";
        var compilation = CSharpCompilation.Create("test", new[] { CSharpSyntaxTree.ParseText(stubs) });

        // The <%# %> lives inside a plain <div>'s attribute, so the parser keeps it
        // in the literal text; it must still be lifted into a DataBoundLiteralControl
        // rather than rendered as raw markup.
        const string markup =
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><div class=\"x_<%# Eval(\"Skin\") %>\">y</div></form>\r\n";
        var additionalTexts = new AdditionalText[] { new InMemoryAdditionalText("/proj/Default.aspx", markup) };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);

        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();
        string text = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("default_aspx.g.cs")).GetText().ToString();

        Assert.Contains("new global::System.Web.UI.DataBoundLiteralControl(2, 1)", text);
        Assert.Contains("global::System.Convert.ToString(Eval(\"Skin\")", text);
        // The raw data-binding syntax must not leak into a LiteralControl.
        Assert.DoesNotContain("<%#", text);
    }

    [Fact]
    public void User_control_registered_by_src_is_built_and_initialized()
    {
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n";
        var compilation = CSharpCompilation.Create("test", new[] { CSharpSyntaxTree.ParseText(stubs) });
        const string markup =
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><uc:Widget id=\"w\" runat=\"server\" /></form>\r\n";
        const string webConfig =
            "<configuration><system.web><pages><controls>" +
            "<add tagPrefix=\"uc\" tagName=\"Widget\" src=\"~/Controls/ctlWidget.ascx\" /></controls></pages></system.web></configuration>";
        var additionalTexts = new AdditionalText[]
        {
            new InMemoryAdditionalText("/proj/Default.aspx", markup),
            new InMemoryAdditionalText("/proj/web.config", webConfig),
        };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);

        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();
        string text = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("default_aspx.g.cs")).GetText().ToString();

        // <uc:Widget> resolves to the user control's generated ASP type, built and
        // initialized, then added to the form.
        Assert.Contains("new global::ASP.controls_ctlwidget_ascx()", text);
        Assert.Contains("InitializeAsUserControl(this.Page)", text);
        Assert.Contains("AddParsedSubObject(__BuildControlw())", text);
    }

    [Fact]
    public void Two_way_bind_generates_an_extract_values_method()
    {
        // A Repeater whose ItemTemplate is [TemplateContainer(.., TwoWay)] with a
        // <%# Bind("Field") %> must produce a CompiledBindableTemplateBuilder and
        // an ExtractValues method that reads the bound control back.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control : IParserAccessor { public string ID { get; set; } public event System.EventHandler DataBinding; public Control BindingContainer => null; public void AddParsedSubObject(object o) { } }\n" +
            "  public class TemplateControl : Control { public object Eval(string e) => null; }\n" +
            "  [System.AttributeUsage(System.AttributeTargets.Property)] public class TemplateContainerAttribute : System.Attribute { public TemplateContainerAttribute(System.Type t, System.ComponentModel.BindingDirection d) { } }\n" +
            "  [System.AttributeUsage(System.AttributeTargets.Class)] public class ParseChildrenAttribute : System.Attribute { public ParseChildrenAttribute(bool childrenAsProperties) { } }\n" +
            "  public interface ITemplate { } }\n" +
            "namespace System.ComponentModel { public enum BindingDirection { OneWay, TwoWay } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace System.Web.UI.WebControls {\n" +
            "  public class CheckBox : System.Web.UI.Control { public bool Checked { get; set; } }\n" +
            "  public class RepeaterItem : System.Web.UI.Control { }\n" +
            "  [System.Web.UI.ParseChildren(true)] public class Repeater : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { }\n" +
            "    [System.Web.UI.TemplateContainer(typeof(RepeaterItem), System.ComponentModel.BindingDirection.TwoWay)] public System.Web.UI.ITemplate ItemTemplate { get; set; } } }\n";
        // The stub attributes must bind, which needs System.Attribute et al.
        var references = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var compilation = CSharpCompilation.Create("test", new[] { CSharpSyntaxTree.ParseText(stubs) }, references);
        const string markup =
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><asp:Repeater id=\"r\" runat=\"server\"><ItemTemplate>" +
            "<asp:CheckBox id=\"ck\" runat=\"server\" Checked='<%# Bind(\"Active\") %>' /></ItemTemplate></asp:Repeater></form>\r\n";
        var additionalTexts = new AdditionalText[] { new InMemoryAdditionalText("/proj/Default.aspx", markup) };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);

        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();
        string text = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("default_aspx.g.cs")).GetText().ToString();

        Assert.Contains("CompiledBindableTemplateBuilder", text);
        Assert.Contains("ExtractTemplateValuesMethod(this.__ExtractValues", text);
        Assert.Contains("FindControl(\"ck\")", text);
        Assert.Contains("[\"Active\"] = ", text);
        Assert.Contains(".Checked;", text);
    }

    [Fact]
    public void TimeSpan_property_is_emitted_via_runtime_TypeConverter()
    {
        // TimeSpan is a Layer-1 type: TypeDescriptor.GetConverter(typeof(TimeSpan))
        // is invoked at design time, ConvertTo(InstanceDescriptor) returns a
        // recipe pointing at TimeSpan.Parse(string), and we emit that verbatim.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace Demo { public class Timer : System.Web.UI.Control { public System.TimeSpan Interval { get; set; } } }\n";
        string text = RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><my:Timer runat=\"server\" ID=\"t\" Interval=\"01:02:03\" /></form>\r\n",
            webConfig: "<configuration><system.web><pages><controls><add tagPrefix=\"my\" namespace=\"Demo\" /></controls></pages></system.web></configuration>");

        Assert.Contains("__ctrl.Interval = global::System.TimeSpan.Parse(\"01:02:03\");", text);
        Assert.DoesNotContain("SetAttribute(\"Interval\"", text);
    }

    [Fact]
    public void Color_hex_value_emits_FromArgb_via_TypeConverter()
    {
        // Color is a Layer-1 type with a converter that returns a 4-arg
        // FromArgb(alpha, r, g, b) InstanceDescriptor for hex input. Named
        // colors take a separate (PropertyInfo) path covered elsewhere.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace System.Drawing { public struct Color { public static Color FromArgb(int a, int r, int g, int b) { return default; } } }\n" +
            "namespace Demo { public class Box : System.Web.UI.Control { public System.Drawing.Color BackColor { get; set; } } }\n";
        // #A1B2C3 isn't a named color, so the converter routes through
        // FromArgb rather than the KnownColor PropertyInfo branch.
        string text = RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><my:Box runat=\"server\" ID=\"b\" BackColor=\"#A1B2C3\" /></form>\r\n",
            webConfig: "<configuration><system.web><pages><controls><add tagPrefix=\"my\" namespace=\"Demo\" /></controls></pages></system.web></configuration>");

        Assert.Contains("__ctrl.BackColor = global::System.Drawing.Color.FromArgb(", text);
        Assert.Contains("161, 178, 195", text);
    }

    [Fact]
    public void Guid_property_is_emitted_via_ctor_recipe()
    {
        // GuidConverter's InstanceDescriptor points at new Guid(string).
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace Demo { public class Tag : System.Web.UI.Control { public System.Guid Key { get; set; } } }\n";
        string text = RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><my:Tag runat=\"server\" ID=\"g\" Key=\"00000000-0000-0000-0000-000000000001\" /></form>\r\n",
            webConfig: "<configuration><system.web><pages><controls><add tagPrefix=\"my\" namespace=\"Demo\" /></controls></pages></system.web></configuration>");

        Assert.Contains("__ctrl.Key = new global::System.Guid(\"00000000-0000-0000-0000-000000000001\");", text);
    }

    [Fact]
    public void System_Type_attribute_emits_typeof_when_target_resolves()
    {
        // Layer 2: a System.Type property becomes typeof(...) — but only when
        // the named type actually resolves in the compilation. Verifies both
        // the success path (Foo resolves → typeof) and the fail path (Missing
        // doesn't resolve → SetAttribute fallback).
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } }\n" +
            "  public interface IAttributeAccessor { void SetAttribute(string name, string value); } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace Demo { public class Picker : System.Web.UI.Control, System.Web.UI.IAttributeAccessor { public System.Type DataType { get; set; } public void SetAttribute(string n, string v) { } }\n" +
            "  public class Foo { } }\n";
        string text = RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\">" +
            "<my:Picker runat=\"server\" ID=\"p1\" DataType=\"Demo.Foo\" />" +
            "<my:Picker runat=\"server\" ID=\"p2\" DataType=\"Demo.MissingType\" />" +
            "</form>\r\n",
            webConfig: "<configuration><system.web><pages><controls><add tagPrefix=\"my\" namespace=\"Demo\" /></controls></pages></system.web></configuration>");

        // Resolves → typeof.
        Assert.Contains("__ctrl.DataType = typeof(global::Demo.Foo);", text);
        // Doesn't resolve → falls through to SetAttribute (because Picker IS IAttributeAccessor).
        Assert.Contains("SetAttribute(\"DataType\", \"Demo.MissingType\")", text);
    }

    [Fact]
    public void Layer3_custom_type_with_Parse_emits_Parse_and_reports_TWF002()
    {
        // A consumer-defined type with public static T Parse(string) and no
        // TypeConverter falls through to Layer 3: codegen emits Type.Parse,
        // and the generator surfaces TWF002 telling the author to attach a
        // [TypeConverter] for richer codegen.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace Demo {\n" +
            "  public struct Coord { public static Coord Parse(string s) => default; }\n" +
            "  public class Marker : System.Web.UI.Control { public Coord Position { get; set; } } }\n";
        string text = RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><my:Marker runat=\"server\" ID=\"m\" Position=\"1,2\" /></form>\r\n",
            out GeneratorDriverRunResult result,
            webConfig: "<configuration><system.web><pages><controls><add tagPrefix=\"my\" namespace=\"Demo\" /></controls></pages></system.web></configuration>");

        Assert.Contains("__ctrl.Position = global::Demo.Coord.Parse(\"1,2\");", text);
        Diagnostic diagnostic = Assert.Single(result.Results.Single().Diagnostics, d => d.Id == "TWF002");
        Assert.Contains("Demo.Coord", diagnostic.GetMessage());
    }

    [Fact]
    public void Layer3_custom_type_with_string_ctor_emits_new_and_reports_TWF002()
    {
        // Same Layer-3 fallback, but the type exposes a public ctor(string)
        // instead of Parse. Output uses 'new Type(value)'.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace Demo {\n" +
            "  public class Tag { public Tag(string raw) { } }\n" +
            "  public class Item : System.Web.UI.Control { public Tag Label { get; set; } } }\n";
        string text = RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><my:Item runat=\"server\" ID=\"i\" Label=\"hello\" /></form>\r\n",
            out GeneratorDriverRunResult result,
            webConfig: "<configuration><system.web><pages><controls><add tagPrefix=\"my\" namespace=\"Demo\" /></controls></pages></system.web></configuration>");

        Assert.Contains("__ctrl.Label = new global::Demo.Tag(\"hello\");", text);
        Assert.Single(result.Results.Single().Diagnostics, d => d.Id == "TWF002");
    }

    [Fact]
    public void Layer3_TWF002_is_deduplicated_within_one_page()
    {
        // Two attributes using the same Layer-3 type should fire TWF002 once,
        // not twice (collected via per-page HashSet keyed by type name).
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace Demo {\n" +
            "  public struct Coord { public static Coord Parse(string s) => default; }\n" +
            "  public class Marker : System.Web.UI.Control { public Coord Start { get; set; } public Coord End { get; set; } } }\n";
        RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><my:Marker runat=\"server\" ID=\"m\" Start=\"1,2\" End=\"3,4\" /></form>\r\n",
            out GeneratorDriverRunResult result,
            webConfig: "<configuration><system.web><pages><controls><add tagPrefix=\"my\" namespace=\"Demo\" /></controls></pages></system.web></configuration>");

        Assert.Single(result.Results.Single().Diagnostics, d => d.Id == "TWF002");
    }

    [Fact]
    public void HtmlHead_drops_whitespace_only_literal_runs()
    {
        // WebFormsContract.DiscardsWhitespaceLiterals: inside a server <head>
        // whitespace-only literal runs are not emitted as LiteralControls,
        // matching HtmlHeadBuilder. A non-whitespace literal still flows.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlHead : System.Web.UI.Control, System.Web.UI.IParserAccessor { public HtmlHead() { } public HtmlHead(string tag) { } public void AddParsedSubObject(object o) { } }\n" +
            "  public class HtmlTitle : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n";
        string text = RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<html><head runat=\"server\">\r\n  <title>Hello</title>\r\n</head></html>\r\n");

        // The <title> child got promoted (HtmlHead auto-promotion) — confirms
        // we're inside the HtmlHead control path.
        Assert.Contains("new global::System.Web.UI.HtmlControls.HtmlTitle()", text);
        // No literal control just for the surrounding "\n  " / "\n" whitespace.
        // The framework's HtmlHeadBuilder would discard those.
        Assert.DoesNotContain("new global::System.Web.UI.LiteralControl(\"\\r\\n  \")", text);
        Assert.DoesNotContain("new global::System.Web.UI.LiteralControl(\"\\n  \")", text);
    }

    [Fact]
    public void Master_page_registers_each_ContentPlaceHolder_id()
    {
        // .master pages emit a MasterPage subclass that adds each
        // ContentPlaceHolder's lowercased ID to its ContentPlaceHolders
        // collection so content pages can target them.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } }\n" +
            "  public class TemplateControl : Control { } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace System.Web.UI.WebControls { public class ContentPlaceHolder : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace System.Web.UI { public class MasterPage : TemplateControl, IParserAccessor { public System.Collections.Generic.List<string> ContentPlaceHolders { get; } = new System.Collections.Generic.List<string>(); public void AddParsedSubObject(object o) { } } }\n";
        const string markup =
            "<%@ Master Inherits=\"Site.Master\" %>\r\n" +
            "<form id=\"f\" runat=\"server\">" +
            "<asp:ContentPlaceHolder ID=\"Main\" runat=\"server\" />" +
            "<asp:ContentPlaceHolder ID=\"Footer\" runat=\"server\" />" +
            "</form>\r\n";
        var compilation = CSharpCompilation.Create("test",
            new[] { CSharpSyntaxTree.ParseText(stubs) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var additionalTexts = new AdditionalText[] { new InMemoryAdditionalText("/proj/Site.master", markup) };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);
        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();
        string text = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("site_master.g.cs")).GetText().ToString();

        Assert.Contains("ContentPlaceHolders.Add(\"main\");", text);
        Assert.Contains("ContentPlaceHolders.Add(\"footer\");", text);
    }

    [Fact]
    public void OnClick_attribute_wires_event_to_codebehind_method()
    {
        // "OnClick=Method" on a control with a Click event AND a method of
        // that name on the codebehind becomes `__ctrl.Click += this.Method;`.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } public event System.EventHandler Click; } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace System.Web.UI.WebControls { public class Button : System.Web.UI.Control { } }\n" +
            "namespace Foo { public class Bar : System.Web.UI.Control { public void OnSave(object sender, System.EventArgs e) { } } }\n";
        string text = RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"><asp:Button runat=\"server\" ID=\"btn\" OnClick=\"OnSave\" /></form>\r\n");

        Assert.Contains("__ctrl.Click += this.OnSave;", text);
    }

    [Fact]
    public void Global_asax_emits_ASP_global_asax_inheriting_from_codebehind()
    {
        // global.asax compiles to ASP._global_asax : <codebehind HttpApplication>.
        // Without it, a non-updatable precompiled app never runs Application_Start.
        var compilation = CSharpCompilation.Create("test");
        const string markup = "<%@ Application Inherits=\"Foo.Global\" %>\r\n";
        var additionalTexts = new AdditionalText[] { new InMemoryAdditionalText("/proj/Global.asax", markup) };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);
        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();
        string text = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("_global_asax.g.cs")).GetText().ToString();

        Assert.Contains("public class _global_asax : global::Foo.Global", text);
    }

    [Fact]
    public void Global_asax_falls_back_to_HttpApplication_when_inherits_missing()
    {
        // No Inherits attribute → base HttpApplication. Lets us serve a
        // global.asax with no codebehind class.
        var compilation = CSharpCompilation.Create("test");
        var additionalTexts = new AdditionalText[] { new InMemoryAdditionalText("/proj/Global.asax", "<%@ Application %>\r\n") };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);
        string text = driver.RunGenerators(compilation).GetRunResult()
            .GeneratedTrees.Single(t => t.FilePath.EndsWith("_global_asax.g.cs")).GetText().ToString();

        Assert.Contains("public class _global_asax : global::System.Web.HttpApplication", text);
    }

    [Fact]
    public void User_control_ascx_inherits_from_UserControl_base()
    {
        // <%@ Control %> → UserControl base, no IHttpHandler / no Page-only
        // members (Theme, InitializeCulture). The generated UserControl is
        // distinct from a Page in its frame shape.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } }\n" +
            "  public class TemplateControl : Control { }\n" +
            "  public class UserControl : TemplateControl { } }\n";
        var compilation = CSharpCompilation.Create("test",
            new[] { CSharpSyntaxTree.ParseText(stubs) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var additionalTexts = new AdditionalText[]
        {
            new InMemoryAdditionalText("/proj/Controls/Widget.ascx",
                "<%@ Control Inherits=\"Foo.WidgetCode\" %>\r\n<div>hello</div>\r\n"),
        };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);
        // Type name = virtual-path-mangled (lowercase, '/'→'_', '.'→'_').
        // /Controls/Widget.ascx → controls_widget_ascx.
        string text = driver.RunGenerators(compilation).GetRunResult()
            .GeneratedTrees.Single(t => t.FilePath.EndsWith("controls_widget_ascx.g.cs")).GetText().ToString();

        Assert.Contains("public class controls_widget_ascx : global::Foo.WidgetCode", text);
        // UserControls don't have ProcessRequest / GetTypeHashCode / IHttpHandler.
        Assert.DoesNotContain("IHttpHandler", text);
        Assert.DoesNotContain("ProcessRequest", text);
        // ... or Page-only ctor calls.
        Assert.DoesNotContain("InitializeCulture()", text);
    }

    [Fact]
    public void All_four_page_defaults_emit_when_set_in_directive()
    {
        // EmitPageDefaults' table drives four properties; the existing
        // happy-path test exercises Theme + EnableEventValidation via
        // web.config. This one pins the other two
        // (MaintainScrollPositionOnPostBack + StyleSheetTheme) and the
        // directive-side override path.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } }\n" +
            "  public class Page : Control { public string Theme { get; set; } public string StyleSheetTheme { get; set; } public bool EnableEventValidation { get; set; } public bool MaintainScrollPositionOnPostBack { get; set; } public void InitializeCulture() { } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n";
        string text = RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" MaintainScrollPositionOnPostBack=\"true\" StyleSheetTheme=\"Skin1\" Theme=\"Skin2\" EnableEventValidation=\"true\" %>\r\n" +
            "<form id=\"f\" runat=\"server\"></form>\r\n");

        Assert.Contains("__ctrl.MaintainScrollPositionOnPostBack = true;", text);
        Assert.Contains("__ctrl.StyleSheetTheme = \"Skin1\";", text);
        Assert.Contains("__ctrl.Theme = \"Skin2\";", text);
        Assert.Contains("__ctrl.EnableEventValidation = true;", text);
    }

    [Fact]
    public void Multiple_aspx_files_each_produce_their_own_generated_type()
    {
        // The manifest test already covers count; this verifies each file
        // generates a separately-named type at the right virtual path.
        var compilation = CSharpCompilation.Create("test");
        var additionalTexts = new AdditionalText[]
        {
            new InMemoryAdditionalText("/proj/Forms/A.aspx", "<%@ Page %>"),
            new InMemoryAdditionalText("/proj/Forms/B.aspx", "<%@ Page %>"),
            new InMemoryAdditionalText("/proj/Controls/C.ascx", "<%@ Control %>"),
        };
        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: null,
            optionsProvider: options);
        GeneratorDriverRunResult result = driver.RunGenerators(compilation).GetRunResult();

        Assert.Single(result.GeneratedTrees, t => t.FilePath.EndsWith("a_aspx.g.cs"));
        Assert.Single(result.GeneratedTrees, t => t.FilePath.EndsWith("b_aspx.g.cs"));
        Assert.Single(result.GeneratedTrees, t => t.FilePath.EndsWith("c_ascx.g.cs"));
    }

    [Fact]
    public void Link_outside_server_head_maps_to_HtmlGenericControl()
    {
        // aspnet_compiler maps <link runat="server"> to HtmlLink only when
        // it's hosted directly inside a server <head> (HtmlHeadBuilder is
        // the source of the specific binding). Outside that context — a
        // <link> in the body, or directly under the page root — the
        // framework produces HtmlGenericControl. The bug: the resolver
        // mapped <link> to HtmlLink unconditionally; when the codebehind
        // declared `HtmlGenericControl Style`, the field assignment got
        // silently dropped (HtmlLink not assignable to HtmlGenericControl)
        // and the field stayed null at runtime.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } }\n" +
            "  public class HtmlGenericControl : System.Web.UI.Control, System.Web.UI.IParserAccessor { public HtmlGenericControl(string tag) { } public void AddParsedSubObject(object o) { } }\n" +
            "  public class HtmlLink : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace Foo { public class Bar : System.Web.UI.Page { protected System.Web.UI.HtmlControls.HtmlGenericControl Style; } }\n" +
            "namespace System.Web.UI { public class Page : Control { } }\n";
        string text = RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\">" +
            "<link runat=\"server\" id=\"Style\" />" +
            "</form>\r\n");

        // Outside <head>, <link> falls through to HtmlGenericControl with
        // the tag name as the constructor argument.
        Assert.Contains("new global::System.Web.UI.HtmlControls.HtmlGenericControl(\"link\")", text);
        Assert.DoesNotContain("new global::System.Web.UI.HtmlControls.HtmlLink", text);
        // Codebehind field assignment present — the runtime NRE root cause.
        Assert.Contains("this.Style = __ctrl;", text);
    }

    [Fact]
    public void Link_inside_server_head_still_maps_to_HtmlLink()
    {
        // The companion to Link_outside_server_head_maps_to_HtmlGenericControl
        // — under a server <head>, <link> / <meta> / <title> still resolve
        // to their specific Html* types, matching HtmlHeadBuilder.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlHead : System.Web.UI.Control, System.Web.UI.IParserAccessor { public HtmlHead() { } public HtmlHead(string tag) { } public void AddParsedSubObject(object o) { } }\n" +
            "  public class HtmlLink : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace Foo { public class Bar : System.Web.UI.Page { protected System.Web.UI.HtmlControls.HtmlLink Style; } }\n" +
            "namespace System.Web.UI { public class Page : Control { } }\n";
        string text = RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<head runat=\"server\"><link runat=\"server\" id=\"Style\" /></head>\r\n");

        Assert.Contains("new global::System.Web.UI.HtmlControls.HtmlLink()", text);
        Assert.Contains("this.Style = __ctrl;", text);
    }

    [Fact]
    public void Codebehind_field_type_mismatch_emits_TWF003()
    {
        // The resolved control type isn't assignable to the same-named
        // codebehind field. Without the diagnostic the field assignment
        // is silently dropped and the field stays null at runtime (NRE
        // on first access). TWF003 is an Error: the build fails with a
        // fixable message instead.
        const string stubs =
            "namespace System.Web.UI { public interface IParserAccessor { void AddParsedSubObject(object o); }\n" +
            "  public class Control { public string ID { get; set; } } }\n" +
            "namespace System.Web.UI.HtmlControls { public class HtmlForm : System.Web.UI.Control, System.Web.UI.IParserAccessor { public void AddParsedSubObject(object o) { } } }\n" +
            "namespace System.Web.UI.WebControls { public class Label : System.Web.UI.Control { } }\n" +
            "namespace Foo { public class Bar : System.Web.UI.Page { protected System.Web.UI.HtmlControls.HtmlForm Mismatch; } }\n" +
            "namespace System.Web.UI { public class Page : Control { } }\n";
        RunDefaultAspx(stubs,
            "<%@ Page Inherits=\"Foo.Bar\" %>\r\n" +
            "<form id=\"f\" runat=\"server\">" +
            "<asp:Label runat=\"server\" ID=\"Mismatch\" />" +
            "</form>\r\n",
            out GeneratorDriverRunResult result);

        Diagnostic diagnostic = Assert.Single(result.Results.Single().Diagnostics, d => d.Id == "TWF003");
        // Carries the control id, the resolved type and the conflicting field type.
        string message = diagnostic.GetMessage();
        Assert.Contains("Mismatch", message);
        Assert.Contains("System.Web.UI.WebControls.Label", message);
        Assert.Contains("System.Web.UI.HtmlControls.HtmlForm", message);
    }

    // Shared driver: stubs + minimal references, one Default.aspx (or .master if
    // the caller-supplied markup says so), optional web.config. Returns the
    // generated default_aspx.g.cs text. Eliminates the boilerplate around
    // setting up a CSharpCompilation + GeneratorDriver per test.
    private static string RunDefaultAspx(string stubs, string markup, string? webConfig = null)
        => RunDefaultAspx(stubs, markup, out _, webConfig);

    private static string RunDefaultAspx(string stubs, string markup, out GeneratorDriverRunResult result, string? webConfig = null)
    {
        var references = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var compilation = CSharpCompilation.Create("test", new[] { CSharpSyntaxTree.ParseText(stubs) }, references);
        var texts = new List<AdditionalText>
        {
            new InMemoryAdditionalText("/proj/Default.aspx", markup),
        };
        if (webConfig != null)
        {
            texts.Add(new InMemoryAdditionalText("/proj/web.config", webConfig));
        }

        var options = new TestOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectDirectory"] = "/proj",
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new MarkupSourceGenerator().AsSourceGenerator() },
            additionalTexts: texts,
            parseOptions: null,
            optionsProvider: options);
        result = driver.RunGenerators(compilation).GetRunResult();
        return result.GeneratedTrees.Single(t => t.FilePath.EndsWith("default_aspx.g.cs")).GetText().ToString();
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly string _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = text;
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default)
        {
            return SourceText.From(_text);
        }
    }

    private sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _global;

        public TestOptionsProvider(Dictionary<string, string> global)
        {
            _global = new TestOptions(global);
        }

        public override AnalyzerConfigOptions GlobalOptions => _global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _global;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _global;
    }

    private sealed class TestOptions : AnalyzerConfigOptions
    {
        private readonly Dictionary<string, string> _values;

        public TestOptions(Dictionary<string, string> values)
        {
            _values = values;
        }

        public override bool TryGetValue(string key, out string value)
        {
            return _values.TryGetValue(key, out value!);
        }
    }
}
