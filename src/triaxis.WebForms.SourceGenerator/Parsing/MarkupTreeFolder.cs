using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Compilation;
using triaxis.WebForms.SourceGenerator.Model;

namespace triaxis.WebForms.SourceGenerator.Parsing
{
    /// <summary>
    /// Folds the vendored parser's flat event stream into a control tree.
    /// Tags become <see cref="ServerControlNode"/>s when they carry
    /// <c>runat="server"</c> or use a registered control prefix; everything
    /// else (text and plain markup tags) is concatenated byte-faithfully into
    /// <see cref="LiteralNode"/>s by slicing the original source.
    /// </summary>
    /// <remarks>
    /// Distinguishing a control's inner *property* elements (e.g. a Telerik
    /// <c>&lt;Scripts&gt;</c> collection) from genuine child controls needs the
    /// control's type metadata (ParseChildren/property shape); this syntactic
    /// fold treats registered-prefix children as controls, which is correct for
    /// container controls (form/panel/…) and approximate for collection/template
    /// properties — the semantic layer refines that next.
    /// </remarks>
    internal sealed class MarkupTreeFolder
    {
        private readonly string _fileText;
        private readonly HashSet<string> _serverPrefixes;
        private readonly Func<string?, string, bool, (string? TypeMetadata, bool IsPropertyContainer)> _resolveChild;
        private readonly Stack<ServerControlNode> _open = new Stack<ServerControlNode>();
        private readonly Stack<StringBuilder> _literals = new Stack<StringBuilder>();
        private readonly Stack<bool> _propertyContainer = new Stack<bool>();
        private readonly Stack<string?> _typeStack = new Stack<string?>();
        // A non-server <table> nested inside a server table is opaque literal: its
        // rows/cells stay verbatim (so a nested </td> never closes the outer server
        // cell) and only runat controls are lifted out as islands — matching how the
        // ASP.NET compiler treats a layout table inside a server table. The counter
        // tracks <table>/</table> nesting in such a region; the stack saves it across
        // a lifted island's own subtree, where promotion resumes normally.
        private readonly Stack<int> _tableLiteralDepthStack = new Stack<int>();
        private int _tableLiteralDepth;
        private readonly List<string> _errors = new List<string>();

        private MarkupTreeFolder(string fileText, IEnumerable<string> serverPrefixes, Func<string?, string, bool, (string?, bool)> resolveChild)
        {
            _fileText = fileText;
            _resolveChild = resolveChild;
            _serverPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "asp" };
            foreach (string prefix in serverPrefixes)
            {
                _serverPrefixes.Add(prefix);
            }
        }

        public static ServerControlNode Fold(
            string filename,
            string fileText,
            IEnumerable<string>? serverPrefixes,
            out IReadOnlyList<string> errors,
            Func<string?, string, bool, (string?, bool)>? resolveChild = null)
        {
            var folder = new MarkupTreeFolder(fileText, serverPrefixes ?? Array.Empty<string>(), resolveChild ?? ((_, _, _) => (null, false)));
            ServerControlNode root = folder.Run(filename);
            errors = folder._errors;
            return root;
        }

        private ServerControlNode Run(string filename)
        {
            var root = new ServerControlNode { RawName = "#document", TagName = "#document" };
            _open.Push(root);
            _literals.Push(new StringBuilder());
            _propertyContainer.Push(false);
            _typeStack.Push(null);

            var parser = new AspParser(filename, new StringReader(_fileText));
            parser.Error += (location, message) => _errors.Add($"L{location.BeginLine}: {message}");
            parser.TextParsed += (location, text) => _literals.Peek().Append(text);
            parser.TagParsed += (location, tagType, id, attributes) =>
                HandleTag(parser, tagType, id, attributes);

            parser.Parse();
            FlushLiteral();
            return root;
        }

        private void HandleTag(AspParser parser, TagType tagType, string id, TagAttributes attributes)
        {
            switch (tagType)
            {
                case TagType.Directive:
                    CaptureRegister(id, attributes);
                    return;

                case TagType.ServerComment:
                    return;

                case TagType.CodeRender:
                    AddCode(CodeRenderKind.Statement, id);
                    return;
                case TagType.CodeRenderExpression:
                    AddCode(CodeRenderKind.Expression, id);
                    return;
                case TagType.CodeRenderEncode:
                    AddCode(CodeRenderKind.EncodedExpression, id);
                    return;
                case TagType.DataBinding:
                    AddCode(CodeRenderKind.DataBinding, id);
                    return;

                case TagType.Tag:
                {
                    bool runat = IsRunAtServer(attributes);

                    // Inside an opaque nested table, only runat controls are lifted
                    // out; everything else (including its tr/td) stays literal, and a
                    // nested <table> deepens the region.
                    if (_tableLiteralDepth > 0 && !runat)
                    {
                        AppendVerbatim(parser);
                        if (IsTable(id))
                        {
                            _tableLiteralDepth++;
                        }

                        return;
                    }

                    bool promoted = _tableLiteralDepth == 0 && IsPromotedElement(id);
                    bool isServer = runat || promoted || (_tableLiteralDepth == 0 && IsServerControl(id, attributes, runat, promoted));
                    if ((_tableLiteralDepth == 0 && _propertyContainer.Peek()) || isServer)
                    {
                        (string? typeMetadata, bool isPropertyContainer) = _resolveChild(_typeStack.Peek(), id, isServer);
                        ServerControlNode node = OpenControl(id, attributes, isServer);
                        node.Promoted = promoted && !runat;
                        node.SourceStart = parser.BeginPosition;
                        _open.Push(node);
                        _literals.Push(new StringBuilder());
                        _propertyContainer.Push(isPropertyContainer);
                        _typeStack.Push(typeMetadata);
                        // A lifted island builds its own tree, so promotion resumes;
                        // the region's depth is restored when the island closes.
                        _tableLiteralDepthStack.Push(_tableLiteralDepth);
                        _tableLiteralDepth = 0;
                    }
                    else
                    {
                        AppendVerbatim(parser);
                        if (IsTable(id))
                        {
                            _tableLiteralDepth++;
                        }
                    }

                    return;
                }

                case TagType.SelfClosing:
                {
                    bool runat = IsRunAtServer(attributes);
                    if (_tableLiteralDepth > 0 && !runat)
                    {
                        AppendVerbatim(parser);
                        return;
                    }

                    bool promoted = _tableLiteralDepth == 0 && IsPromotedElement(id);
                    bool isServer = runat || promoted || (_tableLiteralDepth == 0 && IsServerControl(id, attributes, runat, promoted));
                    if ((_tableLiteralDepth == 0 && _propertyContainer.Peek()) || isServer)
                    {
                        ServerControlNode node = OpenControl(id, attributes, isServer);
                        node.Promoted = promoted && !runat;
                        node.SelfClosing = true;
                        node.SourceStart = parser.BeginPosition;
                        node.SourceEnd = parser.EndPosition;
                    }
                    else
                    {
                        AppendVerbatim(parser);
                    }

                    return;
                }

                case TagType.Close:
                    if (_tableLiteralDepth > 0)
                    {
                        AppendVerbatim(parser);
                        if (IsTable(id))
                        {
                            _tableLiteralDepth--;
                        }
                    }
                    else if (_open.Count > 1 && string.Equals(_open.Peek().RawName, id, StringComparison.OrdinalIgnoreCase))
                    {
                        FlushLiteral();
                        _literals.Pop();
                        _propertyContainer.Pop();
                        _typeStack.Pop();
                        _open.Pop().SourceEnd = parser.EndPosition;
                        _tableLiteralDepth = _tableLiteralDepthStack.Pop();
                    }
                    else
                    {
                        AppendVerbatim(parser);
                    }

                    return;
            }
        }

        private ServerControlNode OpenControl(string rawName, TagAttributes attributes, bool isServer)
        {
            FlushLiteral();
            (string? prefix, string tag) = SplitPrefix(rawName);
            var node = new ServerControlNode
            {
                RawName = rawName,
                Prefix = prefix,
                TagName = tag,
                IsServerControl = isServer,
            };

            if (attributes != null)
            {
                foreach (object key in attributes.Keys)
                {
                    if (key is not string name)
                    {
                        continue;
                    }

                    string value = attributes[key] as string ?? string.Empty;
                    if (string.Equals(name, "runat", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        node.Id = value;
                        continue;
                    }

                    node.Attributes.Add(new MarkupAttribute(name, value));
                }
            }

            _open.Peek().Children.Add(node);
            return node;
        }

        private void AddCode(CodeRenderKind kind, string code)
        {
            FlushLiteral();
            _open.Peek().Children.Add(new CodeRenderNode(kind, code ?? string.Empty));
        }

        private void AppendVerbatim(AspParser parser)
        {
            int start = parser.BeginPosition;
            int end = parser.EndPosition;
            if (start >= 0 && end >= start && end <= _fileText.Length)
            {
                _literals.Peek().Append(_fileText, start, end - start);
            }
        }

        private void FlushLiteral()
        {
            StringBuilder sb = _literals.Peek();
            if (sb.Length > 0)
            {
                _open.Peek().Children.Add(new LiteralNode(sb.ToString()));
                sb.Clear();
            }
        }

        private static bool IsRunAtServer(TagAttributes attributes)
        {
            return attributes != null && attributes.IsRunAtServer();
        }

        // A tag is a server control with runat="server", or — inside a property
        // container — when it uses a registered prefix (a collection/property item
        // such as <asp:BoundColumn> in a grid's Columns, which omits runat), or
        // when auto-promoted (table rows/cells, head elements). A registered-prefix
        // tag in a *control* container without runat (e.g. <asp:Label> used as a
        // JS literal placeholder) is passed through as markup, matching ASP.NET.
        private bool IsServerControl(string rawName, TagAttributes attributes, bool runat, bool promoted)
        {
            if (runat || promoted)
            {
                return true;
            }

            (string? prefix, _) = SplitPrefix(rawName);
            return _propertyContainer.Peek() && prefix != null && _serverPrefixes.Contains(prefix);
        }

        // Server containers that auto-promote specific child tags to server
        // controls without a runat attribute. <head> manages title/meta/link/
        // base; a server <table> parses <tr> into Rows; <tr> parses
        // <td>/<th> into Cells. Everything else stays literal. ASP.NET's
        // parser encodes this promotion in HtmlHeadBuilder / HtmlTableBuilder
        // / HtmlTableRowBuilder (System.Web-internal types unavailable to
        // the analyzer), so the data is mirrored here. Add a parent entry
        // when supporting a new container with its own ControlBuilder-driven
        // child promotion.
        private static readonly Dictionary<string, HashSet<string>> s_promotedChildren =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                ["System.Web.UI.HtmlControls.HtmlHead"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "title", "meta", "link", "base" },
                ["System.Web.UI.HtmlControls.HtmlTable"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tr" },
                ["System.Web.UI.HtmlControls.HtmlTableRow"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "td", "th" },
            };

        private static bool IsTable(string rawName)
        {
            return string.Equals(rawName, "table", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPromotedElement(string rawName)
        {
            return _typeStack.Peek() is { } parent
                && s_promotedChildren.TryGetValue(parent, out HashSet<string> tags)
                && tags.Contains(rawName);
        }

        private void CaptureRegister(string id, TagAttributes attributes)
        {
            if (!string.Equals(id, "Register", StringComparison.OrdinalIgnoreCase) || attributes == null)
            {
                return;
            }

            if (attributes["tagprefix"] is string prefix && !string.IsNullOrWhiteSpace(prefix))
            {
                _serverPrefixes.Add(prefix);
            }
        }

        private static (string? Prefix, string Tag) SplitPrefix(string rawName)
        {
            int colon = rawName.IndexOf(':');
            return colon < 0
                ? (null, rawName)
                : (rawName.Substring(0, colon), rawName.Substring(colon + 1));
        }
    }
}
