using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Text;
using triaxis.WebForms.SourceGenerator.Model;

namespace triaxis.WebForms.SourceGenerator.Emit
{
    /// <summary>
    /// Emits the control-tree half of a generated page: the body of
    /// <c>__BuildControlTree</c> plus one <c>__BuildControl&lt;name&gt;()</c>
    /// method per server control. Literals and children are wired with
    /// <c>AddParsedSubObject</c>, mirroring the ASP.NET compiler's shape.
    /// </summary>
    /// <remarks>
    /// The tree body and each build method are written through their own
    /// <see cref="IndentedTextWriter"/> at the right starting indent, then the
    /// finished method fragments are accumulated into <see cref="_methods"/> in
    /// the order the ASP.NET compiler emits them (deepest children first).
    /// </remarks>
    internal sealed class ControlTreeEmitter
    {
        private const string ParserAccessor = "global::System.Web.UI.IParserAccessor";
        private const string AttributeAccessor = "global::System.Web.UI.IAttributeAccessor";
        private const string LiteralControl = "global::System.Web.UI.LiteralControl";

        // The indent of a class member (a build method header) and of a method
        // body, in IndentedTextWriter steps of four spaces.
        private const int MemberIndent = 2;
        private const int BodyIndent = 3;

        private readonly ControlTypeResolver _resolver;
        private readonly string _fileText;
        private readonly AttributeBinder _binder;
        // Returns (assignable-field-name, conflicting-existing-type-display):
        //   (name, null)  → field exists and resolved control type is assignable
        //   (null, type)  → field exists but resolved control type is NOT assignable
        //                   (silent drop bug surfaces as TWF003)
        //   (null, null)  → no such member; emitter declares one
        private readonly Func<string, string, (string? AssignableField, string? ConflictingType)> _resolveCodeBehindField;
        private readonly Func<string, bool> _codeBehindHasMember;
        private readonly DefaultContentResolver _defaultContent;
        private readonly HashSet<string> _declaredFields = new HashSet<string>(StringComparer.Ordinal);
        private readonly ChildClassifier _classify;
        private readonly ControlContainerPredicate _isControlContainer;
        private readonly Func<string, bool> _isThemeable;
        private readonly Func<string, bool> _isControl;
        private readonly Func<string, string> _userControlBindingType;
        private readonly string _themeTarget;
        private readonly IReadOnlyDictionary<string, string> _pageDefaults;
        private readonly string _pageTypeMetadata;
        private readonly StringBuilder _methods = new StringBuilder();
        private readonly List<string> _diagnostics = new List<string>();
        private readonly HashSet<string> _methodNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _contentPlaceHolderIds = new List<string>();
        // One list per enclosing two-way template; controls inside record their
        // Bind() targets so the template's ExtractValues method can read them back.
        private readonly Stack<List<ExtractBinding>> _extractCollectors = new Stack<List<ExtractBinding>>();
        // Auto-control counter for tags without an explicit ID. Initialised
        // at 1 so the first emitted name is `__control2` — that's where
        // aspnet_compiler's numbering also starts (it reserves __control1
        // for the page-level frame's implicit identity).
        private int _autoControl = 1;
        private string? _containerType;
        private bool _inTemplate;

        public ControlTreeEmitter(
            ControlTypeResolver resolver,
            string fileText,
            AttributeBinder binder,
            string pageTypeMetadata,
            ChildClassifier classify,
            ControlContainerPredicate isControlContainer,
            Func<string, bool> isThemeable,
            Func<string, bool> isControl,
            Func<string, string> userControlBindingType,
            string themeTarget,
            IReadOnlyDictionary<string, string> pageDefaults,
            Func<string, string, (string?, string?)>? resolveCodeBehindField = null,
            Func<string, bool>? codeBehindHasMember = null,
            DefaultContentResolver? defaultContent = null)
        {
            _resolver = resolver;
            _fileText = fileText;
            _binder = binder;
            _pageTypeMetadata = pageTypeMetadata;
            _classify = classify;
            _isControlContainer = isControlContainer;
            _isThemeable = isThemeable;
            _isControl = isControl;
            _userControlBindingType = userControlBindingType;
            _themeTarget = themeTarget;
            _pageDefaults = pageDefaults;
            _resolveCodeBehindField = resolveCodeBehindField ?? ((_, _) => (null, null));
            _codeBehindHasMember = codeBehindHasMember ?? (_ => false);
            _defaultContent = defaultContent ?? (_ => null);
        }

        private readonly struct ExtractBinding
        {
            public ExtractBinding(string controlId, string controlType, string property, string field)
            {
                ControlId = controlId;
                ControlType = controlType;
                Property = property;
                Field = field;
            }

            public string ControlId { get; }
            public string ControlType { get; }
            public string Property { get; }
            public string Field { get; }
        }

        public sealed class Result
        {
            public string TreeBody { get; set; } = string.Empty;
            public string Methods { get; set; } = string.Empty;
            public IReadOnlyList<string> Diagnostics { get; set; } = System.Array.Empty<string>();
            public IReadOnlyList<FieldBindingMismatch> FieldBindingMismatches { get; set; } = System.Array.Empty<FieldBindingMismatch>();

            /// <summary>Lowercased IDs of the master's ContentPlaceHolder controls,
            /// which the master ctor must register via ContentPlaceHolders.Add.</summary>
            public IReadOnlyList<string> ContentPlaceHolderIds { get; set; } = System.Array.Empty<string>();
        }

        /// <summary>A control whose resolved type isn't assignable to a
        /// same-named existing codebehind field. The framework would
        /// silently drop the field assignment, leaving the field null at
        /// runtime (NRE on first access); the generator surfaces this as
        /// TWF003 instead.</summary>
        public readonly record struct FieldBindingMismatch(string ControlId, string ResolvedType, string ExistingFieldType);

        private readonly List<FieldBindingMismatch> _fieldBindingMismatches = new List<FieldBindingMismatch>();

        public Result Emit(ServerControlNode root, MarkupDirective directive)
        {
            IndentedTextWriter body = CodeWriter.Create(BodyIndent);

            // Title, page defaults and InitializeCulture are Page-only members;
            // UserControl and MasterPage frames must not reference them.
            if (directive.Kind == MarkupKind.Page)
            {
                if (!string.IsNullOrEmpty(directive.Title))
                {
                    body.Line($"__ctrl.Title = {Literal(directive.Title!)};");
                }

                EmitPageDefaults(directive, body);
            }

            // A content page *or* a nested master sets its MasterPageFile before
            // its <asp:Content> templates are added — without it the framework
            // rejects the content controls at PreInit.
            if ((directive.Kind == MarkupKind.Page || directive.Kind == MarkupKind.Master)
                && !string.IsNullOrEmpty(directive.MasterPageFile))
            {
                body.Line($"__ctrl.MasterPageFile = {Literal(directive.MasterPageFile!)};");
            }

            if (directive.Kind == MarkupKind.Page)
            {
                body.Line("__ctrl.InitializeCulture();");
            }

            // The file-level root is always a control container: its children are
            // controls and literals, regardless of any [ParseChildren(true)] on the
            // base type (UserControl carries it, but that only applies when a user
            // control is nested as a child element, never at its own root).
            EmitChildren(root.Children, "__ctrl", _pageTypeMetadata, body, forceControlContainer: true);

            return new Result
            {
                TreeBody = body.ToSource().TrimEnd('\r', '\n'),
                Methods = _methods.ToString().TrimEnd('\r', '\n'),
                Diagnostics = _diagnostics,
                FieldBindingMismatches = _fieldBindingMismatches,
                ContentPlaceHolderIds = _contentPlaceHolderIds,
            };
        }

        // Page directive / web.config <pages> attributes the ASP.NET compiler
        // bakes into the page ctor. To support a new one, add an entry: the
        // attribute name doubles as the lookup key (case-insensitive — both
        // dictionaries use OrdinalIgnoreCase) AND the Page property name.
        // Bool attributes only emit when bool.TryParse succeeds; String
        // attributes only emit when the value is non-empty.
        private static readonly (string AttributeName, PageDefaultKind Kind)[] s_pageDefaultAttributes =
        {
            ("MaintainScrollPositionOnPostBack", PageDefaultKind.Boolean),
            ("EnableEventValidation",            PageDefaultKind.Boolean),
            ("StyleSheetTheme",                  PageDefaultKind.String),
            ("Theme",                            PageDefaultKind.String),
        };

        private enum PageDefaultKind { Boolean, String }

        // Page-wide defaults from <%@ Page %> (highest), web.config <pages>
        // (fallback), baked into the tree the way the ASP.NET compiler does.
        private void EmitPageDefaults(MarkupDirective directive, IndentedTextWriter body)
        {
            foreach ((string name, PageDefaultKind kind) in s_pageDefaultAttributes)
            {
                string? value = directive.Attributes.TryGetValue(name, out string? d) ? d
                    : _pageDefaults.TryGetValue(name, out string? c) ? c
                    : null;
                if (value is null)
                {
                    continue;
                }

                switch (kind)
                {
                    case PageDefaultKind.Boolean:
                        if (bool.TryParse(value, out bool b))
                        {
                            body.Line($"__ctrl.{name} = {(b ? "true" : "false")};");
                        }
                        break;
                    case PageDefaultKind.String:
                        if (value.Length > 0)
                        {
                            body.Line($"__ctrl.{name} = {Literal(value)};");
                        }
                        break;
                }
            }
        }

        private void EmitChildren(IReadOnlyList<MarkupNode> children, string parentVar, string parentMetadata, IndentedTextWriter w, bool forceControlContainer = false, bool templateBody = false)
        {
            bool controlContainer = forceControlContainer || _isControlContainer(parentMetadata);

            // Property containers take no literal/text content.
            if (!controlContainer)
            {
                foreach (MarkupNode child in children)
                {
                    if (child is ServerControlNode control)
                    {
                        EmitChild(control, parentVar, parserLocal: null, parentMetadata, controlContainer: false, w, templateBody);
                    }
                }

                return;
            }

            // Code blocks (<%= %>, <%: %>, <% %>) anywhere in the content switch
            // the whole region to a render delegate: child controls are still
            // added (so they occupy parameterContainer.Controls) but literals and
            // code render through the method, matching the ASP.NET compiler.
            bool hasCodeBlocks = false;
            foreach (MarkupNode child in children)
            {
                // Code renders as either a CodeRenderNode (<%= %> between tags) or
                // embedded in literal text (<%= %> inside a non-server tag's
                // attribute, kept verbatim by the parser).
                if ((child is CodeRenderNode code && code.Kind != CodeRenderKind.DataBinding)
                    || (child is LiteralNode literalNode && LiteralHasRenderCode(literalNode.Text)))
                {
                    hasCodeBlocks = true;
                    break;
                }
            }

            if (hasCodeBlocks)
            {
                EmitRenderMethodContainer(children, parentVar, parentMetadata, w);
                return;
            }

            // Control containers: a maximal run of literals + <%# %> data-binding
            // becomes one LiteralControl (no binding) or DataBoundLiteralControl.
            // Some builders (HtmlHead) discard whitespace-only literal runs.
            bool discardWhitespace = DiscardsWhitespaceLiterals(parentMetadata);
            // Hoist the IParserAccessor cast into a local once per block —
            // aspnet_compiler does this so a method that adds N parsed
            // sub-objects emits N `localN.AddParsedSubObject(...)` calls
            // against a single cast instead of N inline casts. Use the local
            // only when something downstream will actually call into it; an
            // empty container would otherwise see an unused-local warning.
            string? parserLocal = TryEmitParserLocal(children, parentVar, parentMetadata, w, controlContainer: true, discardWhitespace);
            var run = new List<(bool IsDataBind, string Text)>();
            foreach (MarkupNode child in children)
            {
                switch (child)
                {
                    case LiteralNode literal:
                        // A <%# … %> embedded in a non-server tag's attribute
                        // (<div class="x_<%# Container.Skin %>">) is kept verbatim by
                        // the parser, so split it out of the literal text here; this
                        // run carries no render code (that routes to the delegate
                        // path above), only static text and data-binding.
                        foreach ((char kind, string text) in CodeRender.Split(literal.Text))
                        {
                            run.Add((kind == '#', kind == '#' ? text.Replace("Bind(", "Eval(") : text));
                        }
                        break;
                    case CodeRenderNode { Kind: CodeRenderKind.DataBinding } bind:
                        run.Add((true, bind.Code.Replace("Bind(", "Eval(")));
                        break;
                    case ServerControlNode control:
                        FlushRun(run, parentVar, parserLocal, w, discardWhitespace);
                        EmitChild(control, parentVar, parserLocal, parentMetadata, controlContainer: true, w, templateBody);
                        break;
                    case CodeRenderNode code:
                        FlushRun(run, parentVar, parserLocal, w, discardWhitespace);
                        _diagnostics.Add($"code-render ({code.Kind}) in content not yet emitted: {Truncate(code.Code)}");
                        break;
                }
            }

            FlushRun(run, parentVar, parserLocal, w, discardWhitespace);
        }

        private string? TryEmitParserLocal(IReadOnlyList<MarkupNode> children, string parentVar, string parentMetadata, IndentedTextWriter w, bool controlContainer, bool discardWhitespace)
        {
            // Server controls in a control container route through
            // AddParsedSubObject; a literal run does too unless every literal
            // is whitespace and the parent discards whitespace.
            bool any = false;
            foreach (MarkupNode child in children)
            {
                if (child is ServerControlNode) { any = true; break; }
                if (child is LiteralNode lit && !(discardWhitespace && string.IsNullOrWhiteSpace(lit.Text))) { any = true; break; }
                if (child is CodeRenderNode { Kind: CodeRenderKind.DataBinding }) { any = true; break; }
            }
            if (!any)
            {
                return null;
            }
            string name = UniqueName("__parser");
            w.Line($"{ParserAccessor} {name} = ({ParserAccessor}){parentVar};");
            return name;
        }

        // HtmlHeadBuilder.AllowWhitespaceLiterals() is false: whitespace-only
        // literal runs between the head's server controls are dropped.
        private static bool DiscardsWhitespaceLiterals(string parentMetadata)
        {
            return WebFormsContract.DiscardsWhitespaceLiterals(parentMetadata);
        }

        private void FlushRun(List<(bool IsDataBind, string Text)> run, string parentVar, string? parserLocal, IndentedTextWriter w, bool discardWhitespace)
        {
            if (run.Count == 0)
            {
                return;
            }

            bool hasDataBind = run.Exists(s => s.IsDataBind);
            bool containerOk = !run.Exists(s => s.IsDataBind && s.Text.Contains("Container")) || _containerType != null;
            if (hasDataBind && containerOk)
            {
                EmitDataBoundLiteralControl(run, parentVar, parserLocal, w);
            }
            else
            {
                // Plain literal (or data-binding we can't type) — concatenate text.
                var text = new StringBuilder();
                foreach ((bool isDataBind, string segment) in run)
                {
                    if (!isDataBind)
                    {
                        text.Append(segment);
                    }
                }

                string literal = text.ToString();
                if (literal.Length > 0 && !(discardWhitespace && string.IsNullOrWhiteSpace(literal)))
                {
                    w.Line($"{ParserCall(parentVar, parserLocal)}.AddParsedSubObject(new {LiteralControl}({Literal(literal)}));");
                }
            }

            run.Clear();
        }

        // Builds the LHS of an `AddParsedSubObject` call: the hoisted local
        // when EmitChildren allocated one for this block, the inline cast
        // when it didn't.
        private static string ParserCall(string parentVar, string? parserLocal)
            => parserLocal ?? $"(({ParserAccessor}){parentVar})";

        private void EmitDataBoundLiteralControl(List<(bool IsDataBind, string Text)> run, string parentVar, string? parserLocal, IndentedTextWriter w)
        {
            // Interleave into statics (count = databinds + 1) and databinds, the
            // shape DataBoundLiteralControl renders: static[0] db[0] static[1] …
            var statics = new List<string>();
            var binds = new List<string>();
            var current = new StringBuilder();
            foreach ((bool isDataBind, string text) in run)
            {
                if (isDataBind)
                {
                    statics.Add(current.ToString());
                    current.Clear();
                    binds.Add(text);
                }
                else
                {
                    current.Append(text);
                }
            }

            statics.Add(current.ToString());

            string build = UniqueName("__BuildControl_dblc");
            string handler = "__DataBind" + build;
            w.Line($"{ParserCall(parentVar, parserLocal)}.AddParsedSubObject({build}());");

            IndentedTextWriter b = CodeWriter.Create(MemberIndent);
            b.Line(PageFrameEmitter.DebuggerNonUserCode);
            using (b.Block($"private global::System.Web.UI.DataBoundLiteralControl {build}()"))
            {
                b.Line($"global::System.Web.UI.DataBoundLiteralControl __ctrl = new global::System.Web.UI.DataBoundLiteralControl({statics.Count}, {binds.Count});");
                for (int i = 0; i < statics.Count; i++)
                {
                    b.Line($"__ctrl.SetStaticString({i}, {Literal(statics[i])});");
                }

                b.Line($"__ctrl.DataBinding += new global::System.EventHandler(this.{handler});");
                b.Line("return __ctrl;");
            }

            b.Blank();
            using (b.Block($"public void {handler}(object sender, global::System.EventArgs e)"))
            {
                b.Line("global::System.Web.UI.DataBoundLiteralControl __ctrl = (global::System.Web.UI.DataBoundLiteralControl)sender;");
                if (_containerType != null && binds.Exists(x => x.Contains("Container")))
                {
                    b.Line($"global::{_containerType} Container = (global::{_containerType})((global::System.Web.UI.Control)__ctrl).BindingContainer;");
                }

                for (int j = 0; j < binds.Count; j++)
                {
                    b.Line($"__ctrl.SetDataBoundString({j}, global::System.Convert.ToString({binds[j]}, global::System.Globalization.CultureInfo.CurrentCulture));");
                }
            }

            b.Blank();
            _methods.Append(b.ToSource());
        }

        private void EmitChild(ServerControlNode control, string parentVar, string? parserLocal, string parentMetadata, bool controlContainer, IndentedTextWriter w, bool templateBody = false)
        {
            // <script runat="server"> injects its code as members of the page type.
            if (control.IsServerControl && string.Equals(control.TagName, "script", StringComparison.OrdinalIgnoreCase))
            {
                var code = new StringBuilder();
                foreach (MarkupNode c in control.Children)
                {
                    if (c is LiteralNode lit)
                    {
                        code.Append(lit.Text);
                    }
                }

                _methods.AppendLine(code.ToString());
                return;
            }

            // <asp:Content ContentPlaceHolderID="x"> is not a control — its
            // ContentPlaceHolderID setter throws. aspnet registers each as a
            // template via Page.AddContentTemplate.
            if (string.Equals(control.Prefix, "asp", StringComparison.OrdinalIgnoreCase)
                && string.Equals(control.TagName, "Content", StringComparison.OrdinalIgnoreCase))
            {
                EmitContentTemplate(control, w);
                return;
            }

            // In a control container every server child is a parsed sub-object;
            // the property-element classification only applies under a property
            // container (e.g. a [ParseChildren(true)] control used as a child).
            ChildPlacement placement = controlContainer
                ? ChildPlacement.ParsedSubObject()
                : _classify(parentMetadata, control.TagName, control.IsServerControl);
            switch (placement.Kind)
            {
                case ChildPlacementKind.ParsedSubObject:
                {
                    EmittedControl? built = EmitControl(control, parentMetadata, templateBody);
                    if (built is { } b)
                    {
                        w.Line($"{b.TypeName} {b.LocalName} = {b.Method}();");
                        w.Line($"{ParserCall(parentVar, parserLocal)}.AddParsedSubObject({b.LocalName});");
                    }
                    else if (controlContainer)
                    {
                        string verbatim = Verbatim(control);
                        if (verbatim.Length > 0)
                        {
                            w.Line($"{ParserCall(parentVar, parserLocal)}.AddParsedSubObject(new {LiteralControl}({Literal(verbatim)}));");
                        }
                    }
                    break;
                }

                case ChildPlacementKind.CollectionItem:
                {
                    EmittedControl? built = EmitControl(control, parentMetadata, templateBody);
                    if (built is { } b)
                    {
                        w.Line($"{b.TypeName} {b.LocalName} = {b.Method}();");
                        w.Line($"{parentVar}.{placement.Member}.Add({b.LocalName});");
                    }
                    break;
                }

                case ChildPlacementKind.CollectionElement:
                    // The element wraps a collection property: add its children.
                    foreach (MarkupNode grandChild in control.Children)
                    {
                        if (grandChild is ServerControlNode item)
                        {
                            EmittedControl? built = EmitControl(item, parentMetadata);
                            if (built is { } b)
                            {
                                w.Line($"{b.TypeName} {b.LocalName} = {b.Method}();");
                                w.Line($"{parentVar}.{placement.Member}.Add({b.LocalName});");
                            }
                        }
                    }
                    break;

                case ChildPlacementKind.ObjectElement:
                    // The element wraps a complex object property: set its own
                    // attributes (e.g. <Selecting AllowRowSelect="true">) then
                    // populate its child elements in place.
                    EmitObjectAttributes($"{parentVar}.{placement.Member}", placement.MemberTypeMetadata!, control, w);
                    EmitChildren(control.Children, $"{parentVar}.{placement.Member}", placement.MemberTypeMetadata!, w);
                    break;

                case ChildPlacementKind.Template:
                {
                    string build = UniqueName("__BuildTemplate_" + placement.Member);

                    // Build the template body first (into t), collecting any two-way
                    // Bind() targets, so we know whether an ExtractValues method is
                    // needed before we emit the builder that references it.
                    IndentedTextWriter t = CodeWriter.Create(MemberIndent);
                    t.Line(PageFrameEmitter.DebuggerNonUserCode);
                    using (t.Block($"private void {build}(global::System.Web.UI.Control __ctrl)"))
                    {
                        string? savedContainer = _containerType;
                        bool savedInTemplate = _inTemplate;
                        _containerType = placement.MemberTypeMetadata; // the template's [TemplateContainer] item type
                        // A [TemplateInstance(Single)] template (UpdatePanel.ContentTemplate)
                        // lives in the page's naming container, so its controls bind to
                        // page fields just like ordinary content.
                        _inTemplate = !placement.SingleInstance;
                        if (placement.TwoWay)
                        {
                            _extractCollectors.Push(new List<ExtractBinding>());
                        }

                        EmitChildren(control.Children, "__ctrl", "System.Web.UI.Control", t, templateBody: true);
                        _containerType = savedContainer;
                        _inTemplate = savedInTemplate;
                    }

                    t.Blank();

                    // A [TemplateContainer(..., BindingDirection.TwoWay)] property
                    // takes an IBindableTemplate. Emit an ExtractValues method when
                    // the template carries Bind() write-back, else a null extract —
                    // matching the ASP.NET compiler.
                    string builder;
                    if (placement.TwoWay)
                    {
                        List<ExtractBinding> bindings = _extractCollectors.Pop();
                        string extract = bindings.Count > 0
                            ? "new global::System.Web.UI.ExtractTemplateValuesMethod(this." + EmitExtractValues(build, bindings) + ")"
                            : "(global::System.Web.UI.ExtractTemplateValuesMethod)null";
                        builder = $"new global::System.Web.UI.CompiledBindableTemplateBuilder(new global::System.Web.UI.BuildTemplateMethod(this.{build}), {extract})";
                    }
                    else
                    {
                        builder = $"new global::System.Web.UI.CompiledTemplateBuilder(new global::System.Web.UI.BuildTemplateMethod(this.{build}))";
                    }

                    w.Line($"{parentVar}.{placement.Member} = (global::System.Web.UI.ITemplate){builder};");
                    _methods.Append(t.ToSource());
                    break;
                }

                case ChildPlacementKind.Skip:
                    _diagnostics.Add($"<{control.RawName}>: {placement.Reason}");
                    break;
            }
        }

        // The IBindableTemplate.ExtractValues method for a two-way template: find
        // each Bind()-bound control by ID and read its bound property back into the
        // dictionary keyed by data field, mirroring the ASP.NET compiler.
        private string EmitExtractValues(string templateBuild, List<ExtractBinding> bindings)
        {
            string name = UniqueName("__ExtractValues_" + templateBuild);
            IndentedTextWriter e = CodeWriter.Create(MemberIndent);
            e.Line(PageFrameEmitter.DebuggerNonUserCode);
            using (e.Block($"public global::System.Collections.Specialized.IOrderedDictionary {name}(global::System.Web.UI.Control __container)"))
            {
                e.Line("global::System.Collections.Specialized.OrderedDictionary __table = new global::System.Collections.Specialized.OrderedDictionary();");

                // One FindControl per distinct control, then a write per bound property.
                var byControl = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (ExtractBinding binding in bindings)
                {
                    if (seen.Add(binding.ControlId))
                    {
                        byControl.Add(binding.ControlId);
                    }
                }

                foreach (string controlId in byControl)
                {
                    string local = "__c_" + Sanitize(controlId);
                    string controlType = bindings.Find(b => b.ControlId == controlId).ControlType;
                    e.Line($"{controlType} {local} = ({controlType})__container.FindControl({Literal(controlId)});");
                    using (e.Block($"if ({local} != null)"))
                    {
                        foreach (ExtractBinding binding in bindings)
                        {
                            if (binding.ControlId == controlId)
                            {
                                e.Line($"__table[{Literal(binding.Field)}] = {local}.{binding.Property};");
                            }
                        }
                    }
                }

                e.Line("return __table;");
            }

            e.Blank();
            _methods.Append(e.ToSource());
            return name;
        }

        // Binds a complex-property element's own attributes onto the target object
        // (val.ClientSettings.Selecting.AllowRowSelect = true). Property elements
        // take typed/expando values; event and data-binding attributes on them are
        // rare and not emitted.
        private void EmitObjectAttributes(string targetVar, string typeMetadata, ServerControlNode node, IndentedTextWriter w)
        {
            // OpenControl lifts the id out of the attribute list; a property-element
            // sub-object that is itself a control (a picker's <Calendar>/<DateInput>)
            // still needs its ID set, so re-bind it here when the target has one.
            if (node.Id != null)
            {
                AttributeBinding idBinding = _binder(typeMetadata, "ID", node.Id, _containerType);
                if (idBinding.Kind == AttributeBindingKind.Property)
                {
                    w.Line($"{targetVar}.{idBinding.PropertyName} = {idBinding.ValueExpression};");
                }
            }

            foreach (MarkupAttribute attribute in node.Attributes)
            {
                AttributeBinding binding = _binder(typeMetadata, attribute.Name, attribute.Value, _containerType);
                switch (binding.Kind)
                {
                    case AttributeBindingKind.Property:
                        w.Line($"{targetVar}.{binding.PropertyName} = {binding.ValueExpression};");
                        break;
                    case AttributeBindingKind.Attribute:
                        w.Line($"(({AttributeAccessor}){targetVar}).SetAttribute({Literal(attribute.Name)}, {binding.ValueExpression});");
                        break;
                    case AttributeBindingKind.Skip:
                        _diagnostics.Add($"<{node.RawName}> attribute '{attribute.Name}': {binding.SkipReason}");
                        break;
                    default:
                        _diagnostics.Add($"<{node.RawName}> attribute '{attribute.Name}': {binding.Kind} binding on a property element is not emitted");
                        break;
                }
            }
        }

        private void EmitContentTemplate(ServerControlNode node, IndentedTextWriter w)
        {
            string cph = string.Empty;
            foreach (MarkupAttribute a in node.Attributes)
            {
                if (string.Equals(a.Name, "ContentPlaceHolderID", StringComparison.OrdinalIgnoreCase))
                {
                    cph = a.Value;
                    break;
                }
            }

            string build = "__BuildControl" + MethodSuffix(node);
            w.Line($"this.AddContentTemplate({Literal(cph)}, (global::System.Web.UI.ITemplate)new global::System.Web.UI.CompiledTemplateBuilder(new global::System.Web.UI.BuildTemplateMethod(this.{build})));");

            IndentedTextWriter t = CodeWriter.Create(MemberIndent);
            t.Line(PageFrameEmitter.DebuggerNonUserCode);
            using (t.Block($"private void {build}(global::System.Web.UI.Control __ctrl)"))
            {
                // Content controls live in the page's naming container, so unlike data
                // templates their controls DO bind to codebehind fields (no _inTemplate)
                // — but they are still template controls of this page.
                EmitChildren(node.Children, "__ctrl", "System.Web.UI.Control", t, templateBody: true);
            }

            t.Blank();
            _methods.Append(t.ToSource());
        }

        // Controls are created through HttpRuntime.WebObjectActivator when one is
        // registered (the app wires it via AspNetDependencyInjection), falling back
        // to a plain constructor otherwise — the shape the ASP.NET compiler emits so
        // that constructor-injected controls resolve their dependencies. The
        // activator applies only to Control-derived types built parameterlessly;
        // non-control sub-objects (script references, settings) and controls that
        // need constructor arguments are built directly.
        //
        // A server <table>/<tr> builds its auto-promoted rows/cells with a
        // plain constructor (the table's collection builder), not the
        // activator. List the types whose parent's collection builder owns
        // their construction — adding to this set means "the parent
        // collection builder, not WebObjectActivator, instantiates me".
        private static readonly HashSet<string> s_collectionBuiltTypes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "System.Web.UI.HtmlControls.HtmlTableRow",
                "System.Web.UI.HtmlControls.HtmlTableCell",
            };

        // Construction line, with the optional codebehind-field assignment
        // FUSED into the same expression via chained assignment
        // (T __ctrl = this.<field> = ...;). aspnet_compiler emits this
        // shape so the field is populated atomically with construction —
        // any property setter, framework call, or event wiring that runs
        // later in the method sees a populated field even before the
        // build method returns.
        private void EmitInstantiation(IndentedTextWriter m, ResolvedControl rc, bool promoted, string? boundField)
        {
            string fieldAssign = boundField is null ? string.Empty : $"this.{boundField} = ";
            // A user control's generated ASP type isn't in this compilation, so
            // _isControl can't see it — but it is always a UserControl.
            if (string.IsNullOrEmpty(rc.ConstructorArgument)
                && !(promoted && s_collectionBuiltTypes.Contains(rc.MetadataName))
                && (rc.IsUserControl || _isControl(rc.MetadataName)))
            {
                m.Line("global::System.IServiceProvider __activator = global::System.Web.HttpRuntime.WebObjectActivator;");
                // Ternary direction (positive-then-default) and the re-read
                // of the static in the non-null branch both mirror exactly
                // what aspnet_compiler emits — the resulting IL decompiles
                // identically against the precompiled oracle.
                m.Line($"{rc.TypeName} __ctrl = {fieldAssign}(__activator != null) ? ({rc.TypeName})__activator.GetService(typeof({rc.TypeName})) : new {rc.TypeName}();");
            }
            else
            {
                m.Line($"{rc.TypeName} __ctrl = {fieldAssign}new {rc.TypeName}({rc.ConstructorArgument});");
            }
        }

        // Returned to the caller so the parent can emit:
        //   <TypeName> <LocalName> = <Method>();
        //   parent.AddParsedSubObject(<LocalName>);
        // The field assignment lives INSIDE the build method, fused into
        // the construction line via chained assignment
        // (T __ctrl = this.<field> = new T(...);), so the field is
        // populated before any property setter, framework call, or event
        // wiring can fire inside the method — no call-site touch needed.
        // Null when the control didn't resolve.
        private readonly record struct EmittedControl(string Method, string TypeName, string LocalName);

        private EmittedControl? EmitControl(ServerControlNode node, string parentMetadata, bool directTemplateChild = false)
        {
            // <input>'s concrete HtmlInputControl type depends on its type attribute.
            string? inputType = null;
            foreach (MarkupAttribute attribute in node.Attributes)
            {
                if (string.Equals(attribute.Name, "type", StringComparison.OrdinalIgnoreCase))
                {
                    inputType = attribute.Value;
                    break;
                }
            }

            ResolvedControl? resolved = _resolver.Resolve(node.Prefix, node.TagName, inputType, parentMetadata);
            if (resolved is null)
            {
                _diagnostics.Add($"unresolved control <{node.RawName}> — prefix '{node.Prefix}' has no known namespace");
                return null;
            }

            ResolvedControl rc = resolved.Value;
            if (node.Id != null && string.Equals(rc.MetadataName, WebFormsContract.ContentPlaceHolder, StringComparison.Ordinal))
            {
                _contentPlaceHolderIds.Add(node.Id.ToLowerInvariant());
            }

            string suffix = MethodSuffix(node);
            string method = "__BuildControl" + suffix;

            // Resolve the codebehind field binding BEFORE the instantiation
            // so EmitInstantiation can fuse `this.<field> = ` into the
            // construction expression via chained assignment when it's
            // safe to do so. The chained shape (T __ctrl = this.<f> = …;)
            // only compiles when the field's declared type is exactly the
            // resolved control type — for a user-control field declared
            // as the codebehind base type, the chained `this.<f> = …`
            // expression evaluates to the base type and assigning that
            // back into the derived T local fails. So: chain only when
            // we declare the field ourselves with rc.TypeName; otherwise
            // emit a separate `this.<f> = __ctrl;` line after the
            // construction (the field type is whatever the codebehind
            // declared, an implicit upcast handles it).
            string? boundField = null;
            bool chainableFieldAssignment = false;
            if (node.Id != null && !_inTemplate)
            {
                (string? field, string? conflictingType) = _resolveCodeBehindField(node.Id, rc.MetadataName);
                if (field != null)
                {
                    boundField = field;
                }
                else if (rc.IsUserControl && _codeBehindHasMember(node.Id))
                {
                    // The user control's field can't be type-checked here (its
                    // generated type isn't in this compilation), but it derives
                    // from the codebehind field's type, so the assignment is
                    // validated when the consumer assembly compiles.
                    boundField = Sanitize(node.Id);
                }
                else if (conflictingType != null)
                {
                    // Field exists but the resolved control type isn't
                    // assignable to it. The framework would silently drop the
                    // assignment, leaving the field null at runtime (NRE on
                    // first access). Surface as TWF003 so the build fails
                    // with a fixable diagnostic instead of going green and
                    // crashing in the browser.
                    _fieldBindingMismatches.Add(new FieldBindingMismatch(node.Id, rc.MetadataName, conflictingType));
                }
                else
                {
                    string decl = Sanitize(node.Id);
                    if (_declaredFields.Add(decl))
                    {
                        _methods.AppendLine($"        protected {rc.TypeName} {decl};");
                    }

                    boundField = decl;
                    chainableFieldAssignment = true;
                }
            }

            IndentedTextWriter m = CodeWriter.Create(MemberIndent);
            m.Line(PageFrameEmitter.DebuggerNonUserCode);
            using (m.Block($"private {rc.TypeName} {method}()"))
            {
                EmitInstantiation(m, rc, node.Promoted, chainableFieldAssignment ? boundField : null);
                if (boundField != null && !chainableFieldAssignment)
                {
                    m.Line($"this.{boundField} = __ctrl;");
                }

                // A user control must be initialized (FrameworkInitialize wired) right
                // after construction so its own control tree is built.
                if (rc.IsUserControl)
                {
                    m.Line("((global::System.Web.UI.UserControl)__ctrl).InitializeAsUserControl(this.Page);");
                }

                // A control that is a direct child of a template (content or item
                // template) belongs to the page/control that declared the template, so
                // it gets TemplateControl = this. Controls nested inside a container
                // (Panel, RadPageView) within the template do not. Only real Controls
                // carry the property (not AsyncPostBackTrigger/ListItem collection items).
                if (directTemplateChild && (rc.IsUserControl || _isControl(rc.MetadataName)))
                {
                    m.Line("((global::System.Web.UI.Control)__ctrl).TemplateControl = this;");
                }

                // Themeable controls pick up the page's Theme/skin here, exactly where
                // the ASP.NET compiler emits it (before ID and property assignment).
                if (_isThemeable(rc.MetadataName))
                {
                    m.Line($"((global::System.Web.UI.Control)__ctrl).ApplyStyleSheetSkin({_themeTarget});");
                }

                if (node.Id != null)
                {
                    m.Line($"__ctrl.ID = {Literal(node.Id)};");
                }

                EmitAttributes(node, rc, suffix, m);

                if (rc.MetadataName == WebFormsContract.ContentPlaceHolder && node.Id != null)
                {
                    EmitContentPlaceHolder(node, m);
                }
                else if (_defaultContent(rc.MetadataName) is { } content)
                {
                    EmitDefaultContent(node, content, m);
                }
                else
                {
                    EmitChildren(node.Children, "__ctrl", rc.MetadataName, m);
                }

                m.Line("return __ctrl;");
            }

            m.Blank();
            _methods.Append(m.ToSource());
            return new EmittedControl(method, rc.TypeName, suffix);
        }

        // A user control's own type isn't in this compilation; bind its attributes
        // against the UserControl base so inherited properties (Visible,
        // EnableViewState, …) still resolve. Custom properties declared on its
        // codebehind can't be resolved here and are skipped.
        private void EmitAttributes(ServerControlNode node, ResolvedControl rc, string suffix, IndentedTextWriter m)
        {
            string bindingType = rc.IsUserControl ? _userControlBindingType(rc.MetadataName) : rc.MetadataName;
            foreach (MarkupAttribute attribute in node.Attributes)
            {
                AttributeBinding binding = _binder(bindingType, attribute.Name, attribute.Value, _containerType);
                switch (binding.Kind)
                {
                    case AttributeBindingKind.Property:
                        m.Line($"__ctrl.{binding.PropertyName} = {binding.ValueExpression};");
                        break;
                    case AttributeBindingKind.Attribute:
                        m.Line($"(({AttributeAccessor})__ctrl).SetAttribute({Literal(attribute.Name)}, {binding.ValueExpression});");
                        break;
                    case AttributeBindingKind.Event:
                        m.Line($"__ctrl.{binding.PropertyName} += this.{binding.ValueExpression};");
                        break;
                    case AttributeBindingKind.DataBinding:
                        EmitDataBindingHandler(node, rc, suffix, binding, m);
                        break;
                    case AttributeBindingKind.Skip:
                        _diagnostics.Add($"<{node.RawName}> attribute '{attribute.Name}': {binding.SkipReason}");
                        break;
                }
            }
        }

        private void EmitDataBindingHandler(ServerControlNode node, ResolvedControl rc, string suffix, AttributeBinding binding, IndentedTextWriter m)
        {
            string db = UniqueName("__DataBinding_" + suffix + "_" + (binding.PropertyName ?? binding.AttributeName));
            m.Line($"__ctrl.DataBinding += new global::System.EventHandler(this.{db});");

            IndentedTextWriter d = CodeWriter.Create(MemberIndent);
            using (d.Block($"public void {db}(object sender, global::System.EventArgs e)"))
            {
                d.Line($"{rc.TypeName} target = ({rc.TypeName})sender;");
                if (binding.ValueExpression!.Contains("Container") && _containerType != null)
                {
                    d.Line($"global::{_containerType} Container = (global::{_containerType})((global::System.Web.UI.Control)target).BindingContainer;");
                }

                d.Line(binding.AttributeName != null
                    ? $"(({AttributeAccessor})target).SetAttribute({Literal(binding.AttributeName)}, {binding.ValueExpression});"
                    : $"target.{binding.PropertyName} = {binding.ValueExpression};");
            }

            d.Blank();
            _methods.Append(d.ToSource());

            // Two-way Bind(): record the target so the enclosing template's
            // ExtractValues method reads this property back.
            if (binding.BindField != null && node.Id != null && _extractCollectors.Count > 0)
            {
                _extractCollectors.Peek().Add(new ExtractBinding(node.Id, rc.TypeName, binding.PropertyName!, binding.BindField));
            }
        }

        // A master's <asp:ContentPlaceHolder> instantiates the content page's
        // matching template when one was supplied, otherwise builds its own default
        // content — the mechanism by which a content page's markup reaches the page.
        private void EmitContentPlaceHolder(ServerControlNode node, IndentedTextWriter m)
        {
            string field = "__Template_" + Sanitize(node.Id!);
            if (_declaredFields.Add(field))
            {
                _methods.AppendLine($"        private global::System.Web.UI.ITemplate {field};");
            }

            // Access ContentTemplates / InstantiateInContentPlaceHolder through
            // `this` (the generated master), not a MasterPage cast — they are
            // protected, reachable only via the most-derived type.
            m.Line("((global::System.Web.UI.Control)__ctrl).TemplateControl = this;");
            using (m.Block("if (this.ContentTemplates != null)"))
            {
                m.Line($"{field} = (global::System.Web.UI.ITemplate)this.ContentTemplates[{Literal(node.Id!)}];");
            }

            using (m.Block($"if ({field} != null)"))
            {
                m.Line($"this.InstantiateInContentPlaceHolder((global::System.Web.UI.Control)__ctrl, {field});");
            }

            using (m.Block("else"))
            {
                EmitChildren(node.Children, "__ctrl", "System.Web.UI.WebControls.ContentPlaceHolder", m);
            }
        }

        // Assigns a control's inner literal text to its string default property
        // (ListItem's Text), the WebForms ControlBuilder behavior for a
        // [ParseChildren(true, "<prop>")] string property. An explicit attribute
        // of the same name wins, so it's left to EmitAttributes. ListItemControlBuilder
        // HTML-decodes the text and drops whitespace-only bodies; the base builder
        // keeps the text verbatim (content.HtmlDecode / content.AllowWhitespace).
        private void EmitDefaultContent(ServerControlNode node, DefaultContentProperty content, IndentedTextWriter m)
        {
            foreach (MarkupAttribute attribute in node.Attributes)
            {
                if (string.Equals(attribute.Name, content.PropertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            var raw = new StringBuilder();
            foreach (MarkupNode child in node.Children)
            {
                if (child is LiteralNode literal)
                {
                    raw.Append(literal.Text);
                }
            }

            string text = raw.ToString();
            if (text.Length == 0 || (!content.AllowWhitespace && string.IsNullOrWhiteSpace(text)))
            {
                return;
            }

            string value = content.HtmlDecode ? System.Net.WebUtility.HtmlDecode(text) : text;
            m.Line($"__ctrl.{content.PropertyName} = {Literal(value)};");
        }

        // Content with code blocks renders through a delegate. The ASP.NET compiler
        // turns everything that can be a child control — server controls, and (when
        // the region also data-binds) the static+<%# %> runs as
        // LiteralControl/DataBoundLiteralControl — into children rendered via
        // parameterContainer.Controls[i], while <%= %>/<%: %>/<% %> write inline. A
        // region with no data-binding keeps its static text inline rather than
        // wrapping it in literal children, matching the compiler's simpler shape.
        private void EmitRenderMethodContainer(IReadOnlyList<MarkupNode> children, string parentVar, string parentMetadata, IndentedTextWriter w)
        {
            var segments = new List<(char Kind, string Text, ServerControlNode? Control)>();
            foreach (MarkupNode child in children)
            {
                switch (child)
                {
                    case LiteralNode literal:
                        foreach ((char kind, string text) in CodeRender.Split(literal.Text))
                        {
                            segments.Add((kind, text, null));
                        }
                        break;
                    case CodeRenderNode { Kind: CodeRenderKind.Expression } expression:
                        segments.Add(('=', expression.Code.Trim(), null));
                        break;
                    case CodeRenderNode { Kind: CodeRenderKind.EncodedExpression } encoded:
                        segments.Add((':', encoded.Code.Trim(), null));
                        break;
                    case CodeRenderNode { Kind: CodeRenderKind.Statement } statement:
                        segments.Add((' ', statement.Code.Trim(), null));
                        break;
                    case CodeRenderNode { Kind: CodeRenderKind.DataBinding } bind:
                        segments.Add(('#', bind.Code.Replace("Bind(", "Eval("), null));
                        break;
                    case ServerControlNode control:
                        segments.Add(('C', string.Empty, control));
                        break;
                }
            }

            bool hasDataBind = segments.Exists(s => s.Kind == '#');
            IndentedTextWriter render = CodeWriter.Create(BodyIndent);
            var run = new List<(bool IsDataBind, string Text)>();
            int ordinal = 0;
            // Hoist the IParserAccessor cast for this method's AddParsedSubObject
            // calls — emit when there's at least one segment that turns into one
            // (a captured control or, if the region data-binds, any literal run).
            bool needsParser = segments.Exists(s => s.Kind == 'C' || (hasDataBind && (s.Kind == 'L' || s.Kind == '#')));
            string? parserLocal = null;
            if (needsParser)
            {
                parserLocal = UniqueName("__parser");
                w.Line($"{ParserAccessor} {parserLocal} = ({ParserAccessor}){parentVar};");
            }

            void FlushRun()
            {
                if (run.Count == 0)
                {
                    return;
                }

                if (run.Exists(x => x.IsDataBind))
                {
                    EmitDataBoundLiteralControl(run, parentVar, parserLocal, w);
                    render.Line($"parameterContainer.Controls[{ordinal++}].RenderControl(__w);");
                }
                else
                {
                    string text = string.Concat(run.ConvertAll(x => x.Text));
                    if (text.Length > 0)
                    {
                        w.Line($"{ParserCall(parentVar, parserLocal)}.AddParsedSubObject(new {LiteralControl}({Literal(text)}));");
                        render.Line($"parameterContainer.Controls[{ordinal++}].RenderControl(__w);");
                    }
                }

                run.Clear();
            }

            foreach ((char kind, string text, ServerControlNode? control) in segments)
            {
                switch (kind)
                {
                    // Static text and <%# %> join a run only when the region binds;
                    // otherwise static text writes inline.
                    case 'L' when hasDataBind:
                    case '#':
                        run.Add((kind == '#', text));
                        break;
                    case 'L':
                        EmitRenderSegment('L', text, render);
                        break;
                    case 'C':
                        FlushRun();
                        EmittedControl? built = EmitControl(control!, parentMetadata);
                        if (built is { } b)
                        {
                            w.Line($"{b.TypeName} {b.LocalName} = {b.Method}();");
                            w.Line($"{ParserCall(parentVar, parserLocal)}.AddParsedSubObject({b.LocalName});");
                            render.Line($"parameterContainer.Controls[{ordinal++}].RenderControl(__w);");
                        }
                        break;
                    default:
                        FlushRun();
                        EmitRenderSegment(kind, text, render);
                        break;
                }
            }

            FlushRun();

            string renderName = UniqueName("__Render");
            w.Line($"{parentVar}.SetRenderMethodDelegate(new global::System.Web.UI.RenderMethod(this.{renderName}));");

            IndentedTextWriter r = CodeWriter.Create(MemberIndent);
            using (r.Block($"private void {renderName}(global::System.Web.UI.HtmlTextWriter __w, global::System.Web.UI.Control parameterContainer)"))
            {
                r.Raw(render.ToSource());
            }

            r.Blank();
            _methods.Append(r.ToSource());
        }

        // True when literal markup carries an inline <%= %>/<%: %>/<% %> render
        // block (a <%# %> data-binding does not count — that stays a
        // DataBoundLiteralControl).
        private static bool LiteralHasRenderCode(string text)
        {
            foreach ((char kind, string _) in CodeRender.Split(text))
            {
                if (kind is '=' or ':' or ' ')
                {
                    return true;
                }
            }

            return false;
        }

        private static void EmitRenderSegment(char kind, string text, IndentedTextWriter r)
        {
            switch (kind)
            {
                case 'L':
                    if (text.Length > 0) { r.Line($"__w.Write({Literal(text)});"); }
                    break;
                case '=':
                    r.Line($"__w.Write({text});");
                    break;
                case ':':
                    r.Line($"__w.Write(global::System.Web.HttpUtility.HtmlEncode(global::System.Convert.ToString({text})));");
                    break;
                case ' ':
                    r.Line(text);
                    break;
            }
        }

        private string MethodSuffix(ServerControlNode node)
        {
            return UniqueName(!string.IsNullOrEmpty(node.Id) ? Sanitize(node.Id!) : "__control" + (++_autoControl));
        }

        private string UniqueName(string baseName)
        {
            // Names recur across naming containers / templates; ensure uniqueness
            // within the generated type.
            string name = Sanitize(baseName);
            int suffix = 1;
            while (!_methodNames.Add(name))
            {
                name = Sanitize(baseName) + "_" + (++suffix);
            }

            return name;
        }

        private static string Sanitize(string id)
        {
            var sb = new StringBuilder(id.Length);
            foreach (char c in id)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }

            return sb.ToString();
        }

        private static string Literal(string value) => CodeLiteral.Escape(value);

        private string Verbatim(ServerControlNode node)
        {
            if (node.SourceStart >= 0 && node.SourceEnd > node.SourceStart && node.SourceEnd <= _fileText.Length)
            {
                return _fileText.Substring(node.SourceStart, node.SourceEnd - node.SourceStart);
            }

            return string.Empty;
        }

        private static string Truncate(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 50 ? s : s.Substring(0, 47) + "...";
        }
    }
}
