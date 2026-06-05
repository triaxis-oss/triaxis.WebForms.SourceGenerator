using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using triaxis.WebForms.SourceGenerator.Model;
using triaxis.WebForms.SourceGenerator.Parsing;

namespace triaxis.WebForms.SourceGenerator.Emit
{
    /// <summary>
    /// Compiles one <c>App_Themes\&lt;theme&gt;\*.skin</c> file into the
    /// <c>__controlSkins</c> registrations + per-skin
    /// <c>__BuildControl&lt;N&gt;</c> methods that go inside the theme's
    /// <c>PageTheme</c> subclass. Each top-level server control in the skin
    /// becomes a <c>ControlSkinDelegate</c> that the runtime invokes to apply
    /// property defaults whenever a matching control is instantiated under
    /// the theme; property-element children (e.g. <c>&lt;HeaderStyle&gt;</c>
    /// under <c>&lt;asp:GridView&gt;</c>) recurse into <c>void
    /// __BuildControl&lt;M&gt;(SubType)</c> methods that configure the
    /// sub-object in place.
    /// </summary>
    internal sealed class SkinEmitter
    {
        private readonly ControlTypeResolver _resolver;
        private readonly AttributeBinder _binder;
        private readonly ChildClassifier _classify;
        private readonly Compilation _compilation;
        private readonly IEnumerable<string> _serverPrefixes;
        private readonly System.Func<string?, string, bool, (string?, bool)>? _resolveChild;
        private readonly System.Text.StringBuilder _methods = new System.Text.StringBuilder();
        private readonly List<string> _skinKeys = new List<string>();
        private readonly List<string> _registrations = new List<string>();
        private int _counter;

        public SkinEmitter(
            ControlTypeResolver resolver,
            AttributeBinder binder,
            ChildClassifier classify,
            Compilation compilation,
            IEnumerable<string> serverPrefixes,
            System.Func<string?, string, bool, (string?, bool)>? resolveChild,
            int counterStart)
        {
            _resolver = resolver;
            _binder = binder;
            _classify = classify;
            _compilation = compilation;
            _serverPrefixes = serverPrefixes;
            _resolveChild = resolveChild;
            _counter = counterStart;
        }

        public int Counter => _counter;
        public string Methods => _methods.ToString();
        public IReadOnlyList<string> SkinKeyFields => _skinKeys;
        public IReadOnlyList<string> Registrations => _registrations;

        public void EmitFile(string skinContent, string virtualPath)
        {
            ServerControlNode root = MarkupTreeFolder.Fold(virtualPath, skinContent, _serverPrefixes, out _, _resolveChild);
            foreach (MarkupNode child in root.Children)
            {
                if (child is ServerControlNode top && top.IsServerControl)
                {
                    EmitTopLevel(top);
                }
            }
        }

        private void EmitTopLevel(ServerControlNode node)
        {
            ResolvedControl? resolved = _resolver.Resolve(node.Prefix, node.TagName);
            if (resolved is null)
            {
                return;
            }
            ResolvedControl rc = resolved.Value;
            int id = ++_counter;
            string method = "__BuildControl__control" + id;

            // SkinID="..." picks the named-skin slot; absent → default skin for
            // the type (empty SkinID, one per type per theme).
            string skinId = string.Empty;
            foreach (MarkupAttribute attribute in node.Attributes)
            {
                if (string.Equals(attribute.Name, "SkinID", System.StringComparison.OrdinalIgnoreCase))
                {
                    skinId = attribute.Value;
                    break;
                }
            }

            // Pre-order: allocate the parent's number first, then walk children
            // so they get sequential numbers. Emit the sub-methods first (they
            // must be in scope before the parent calls into them) and the
            // top-level last.
            List<(string Member, int ChildId)> subCalls = new List<(string, int)>();
            foreach (MarkupNode raw in node.Children)
            {
                if (raw is ServerControlNode child && TryEmitPropertyElement(child, rc.MetadataName, out int childId, out string? member))
                {
                    subCalls.Add((member!, childId));
                }
            }

            IndentedTextWriter w = CodeWriter.Create(2);
            w.Line("[global::System.Diagnostics.DebuggerNonUserCode]");
            using (w.Block($"private global::System.Web.UI.Control {method}(global::System.Web.UI.Control ctrl)"))
            {
                w.Line($"{rc.TypeName} val = ({rc.TypeName})ctrl;");
                EmitAttributeAssignments(node, rc.MetadataName, "val", w);
                foreach ((string member, int childId) in subCalls)
                {
                    w.Line($"__BuildControl__control{childId}(val.{member});");
                }
                w.Line("return (global::System.Web.UI.Control)(object)val;");
            }
            w.Blank();
            _methods.Append(w.ToSource());

            string keyName = method + "_skinKey";
            _skinKeys.Add($"private static object {keyName} = global::System.Web.UI.PageTheme.CreateSkinKey(typeof({rc.TypeName}), {CodeLiteral.Escape(skinId)});");
            _registrations.Add($"__controlSkins[{keyName}] = new global::System.Web.UI.ControlSkin(typeof({rc.TypeName}), new global::System.Web.UI.ControlSkinDelegate({method}));");
        }

        private bool TryEmitPropertyElement(ServerControlNode node, string parentMetadata, out int methodId, out string? memberName)
        {
            methodId = 0;
            memberName = null;
            if (!string.IsNullOrEmpty(node.Prefix))
            {
                // Prefixed → another control, not a property element.
                return false;
            }
            ChildPlacement placement = _classify(parentMetadata, node.TagName, node.IsServerControl);
            if (placement.Kind != ChildPlacementKind.ObjectElement || placement.MemberTypeMetadata is null || placement.Member is null)
            {
                return false;
            }

            int id = ++_counter;
            string method = "__BuildControl__control" + id;
            INamedTypeSymbol? memberType = _compilation.GetTypeByMetadataName(placement.MemberTypeMetadata);
            if (memberType is null)
            {
                return false;
            }
            string memberTypeRef = "global::" + memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);

            // Walk deeper before emitting so the local IL hits children before
            // their parent in the source output (matches aspnet_compiler).
            List<(string Member, int ChildId)> nestedCalls = new List<(string, int)>();
            foreach (MarkupNode raw in node.Children)
            {
                if (raw is ServerControlNode nested && TryEmitPropertyElement(nested, placement.MemberTypeMetadata, out int nestedId, out string? nestedMember))
                {
                    nestedCalls.Add((nestedMember!, nestedId));
                }
            }

            IndentedTextWriter w = CodeWriter.Create(2);
            w.Line("[global::System.Diagnostics.DebuggerNonUserCode]");
            using (w.Block($"private void {method}({memberTypeRef} __ctrl)"))
            {
                EmitAttributeAssignments(node, placement.MemberTypeMetadata, "__ctrl", w);
                foreach ((string member, int childId) in nestedCalls)
                {
                    w.Line($"__BuildControl__control{childId}(__ctrl.{member});");
                }
            }
            w.Blank();
            _methods.Append(w.ToSource());

            methodId = id;
            memberName = placement.Member;
            return true;
        }

        private void EmitAttributeAssignments(ServerControlNode node, string typeMetadata, string localName, IndentedTextWriter w)
        {
            foreach (MarkupAttribute attribute in node.Attributes)
            {
                // runat="server" / SkinID are markup metadata, not control properties.
                if (string.Equals(attribute.Name, "runat", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(attribute.Name, "SkinID", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(attribute.Name, "ID", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                AttributeBinding binding = _binder(typeMetadata, attribute.Name, attribute.Value, null);
                if (binding.Kind == AttributeBindingKind.Property && binding.PropertyName != null && binding.ValueExpression != null)
                {
                    w.Line($"{localName}.{binding.PropertyName} = {binding.ValueExpression};");
                }
                // Other binding kinds (Event, Attribute fallback, DataBinding,
                // Skip) don't apply to skin entries — skin attributes are pure
                // property defaults.
            }
        }
    }
}
