using System.Collections.Generic;

namespace triaxis.WebForms.SourceGenerator.Model
{
    internal enum CodeRenderKind
    {
        /// <c>&lt;% ... %&gt;</c> — render statement.
        Statement,

        /// <c>&lt;%= ... %&gt;</c> — render expression.
        Expression,

        /// <c>&lt;%: ... %&gt;</c> — HTML-encoded expression.
        EncodedExpression,

        /// <c>&lt;%# ... %&gt;</c> — data-binding expression.
        DataBinding,
    }

    internal abstract class MarkupNode
    {
    }

    /// <summary>Verbatim markup that becomes a <c>LiteralControl</c> — plain
    /// text plus any non-<c>runat=server</c> tags, byte-faithful to source.</summary>
    internal sealed class LiteralNode : MarkupNode
    {
        public LiteralNode(string text)
        {
            Text = text;
        }

        public string Text { get; }
    }

    /// <summary>A <c>runat="server"</c> control (or registered-prefix tag).</summary>
    internal sealed class ServerControlNode : MarkupNode
    {
        public string? Prefix { get; set; }
        public string TagName { get; set; } = string.Empty;
        public string? Id { get; set; }
        public bool SelfClosing { get; set; }

        /// <summary>True when the tag carries <c>runat="server"</c> or a registered
        /// control prefix. False for property-element tags (e.g. <c>&lt;Scripts&gt;</c>)
        /// captured only because their parent is a property container.</summary>
        public bool IsServerControl { get; set; } = true;

        /// <summary>A literal tag auto-promoted to a server control by its container
        /// (a server &lt;table&gt;'s &lt;tr&gt;/&lt;td&gt;), not by a runat attribute.
        /// Such table rows/cells are built with a plain constructor, not the
        /// WebObjectActivator.</summary>
        public bool Promoted { get; set; }
        public List<MarkupAttribute> Attributes { get; } = new List<MarkupAttribute>();
        public List<MarkupNode> Children { get; } = new List<MarkupNode>();

        /// <summary>Source offsets of this control's full span (open tag through
        /// close tag, or the self-closing tag). Used to emit the verbatim markup
        /// as a literal when the control type can't be resolved.</summary>
        public int SourceStart { get; set; } = -1;
        public int SourceEnd { get; set; } = -1;

        /// <summary>The raw `prefix:tag` (or `tag`) as written — used as the
        /// match key when popping the container on the close tag.</summary>
        public string RawName { get; set; } = string.Empty;
    }

    internal sealed class CodeRenderNode : MarkupNode
    {
        public CodeRenderNode(CodeRenderKind kind, string code)
        {
            Kind = kind;
            Code = code;
        }

        public CodeRenderKind Kind { get; }
        public string Code { get; }
    }

    internal readonly struct MarkupAttribute
    {
        public MarkupAttribute(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }
        public string Value { get; }
    }
}
