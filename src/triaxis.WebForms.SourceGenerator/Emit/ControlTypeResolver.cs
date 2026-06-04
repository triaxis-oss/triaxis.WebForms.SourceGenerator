using System;
using System.Collections.Generic;

namespace triaxis.WebForms.SourceGenerator.Emit
{
    /// <summary>
    /// Maps a markup tag to the .NET control type the generated code
    /// instantiates. <c>asp:</c> and the HTML server tags resolve from built-in
    /// tables (with the <c>System.Web.UI</c> vs <c>System.Web.UI.WebControls</c>
    /// split handled by trying both); other prefixes resolve through the
    /// namespace map gathered from <c>&lt;%@ Register %&gt;</c> and web.config
    /// <c>&lt;pages&gt;&lt;controls&gt;</c>.
    /// </summary>
    /// <remarks>
    /// A caller-supplied <c>typeExists</c> predicate (backed by the compilation)
    /// validates each candidate metadata name, so a tag that doesn't resolve to
    /// a real type — a Telerik property-element, a typo, an abstract base — is
    /// reported as unresolved and the emitter falls back to verbatim literal
    /// rather than emitting a reference that won't compile.
    /// </remarks>
    internal sealed class ControlTypeResolver
    {
        private const string WebControlsNs = "System.Web.UI.WebControls";
        private const string UiNs = "System.Web.UI";
        private const string HtmlControlsNs = "System.Web.UI.HtmlControls";

        // HTML tag → HtmlControl class (unqualified; HtmlControlsNs is
        // prepended). Mirrors ASP.NET's hand-maintained tag→type map: there
        // is no metadata attribute to discover this from
        // (HtmlControl-derived types don't carry an "I handle tag X" marker),
        // so the mapping is data. Add an entry when registering a new
        // HtmlControls type — the type itself must still exist in
        // System.Web.dll (verified by the caller's typeExists predicate).
        private static readonly Dictionary<string, string> s_htmlControls =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = "HtmlAnchor",
                ["form"] = "HtmlForm",
                ["head"] = "HtmlHead",
                ["title"] = "HtmlTitle",
                ["link"] = "HtmlLink",
                ["meta"] = "HtmlMeta",
                ["img"] = "HtmlImage",
                ["button"] = "HtmlButton",
                ["select"] = "HtmlSelect",
                ["textarea"] = "HtmlTextArea",
                ["table"] = "HtmlTable",
                ["tr"] = "HtmlTableRow",
                ["td"] = "HtmlTableCell",
                ["th"] = "HtmlTableCell",
                ["input"] = "HtmlInputText",
            };

        // <input type=…> → concrete HtmlInputControl variant. ASP.NET's
        // HtmlInputControlBuilder picks the type by the type attribute at
        // parse time; mirrored here. Default (missing/unknown type) is
        // HtmlInputText, matching the framework's fallback.
        private static readonly Dictionary<string, string> s_inputControls =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["text"] = "HtmlInputText",
                ["password"] = "HtmlInputPassword",
                ["hidden"] = "HtmlInputHidden",
                ["submit"] = "HtmlInputSubmit",
                ["button"] = "HtmlInputButton",
                ["reset"] = "HtmlInputReset",
                ["checkbox"] = "HtmlInputCheckBox",
                ["radio"] = "HtmlInputRadioButton",
                ["file"] = "HtmlInputFile",
                ["image"] = "HtmlInputImage",
            };

        // Tags that aspnet_compiler maps to their specific Html* type only
        // when hosted directly inside a server <head> — HtmlHeadBuilder is
        // the source of the specific binding, and it's only invoked for
        // children of HtmlHead. Outside that context (e.g. a server-side
        // <link> in the body, or directly under the page root) the
        // framework produces HtmlGenericControl. Map link/meta/title to
        // HtmlLink/HtmlMeta/HtmlTitle only under HtmlHead; tr/td/th have
        // similar parent-context shapes but aren't covered here pending
        // a corpus-driven audit.
        private static readonly HashSet<string> s_headOnlyTags =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "title", "link", "meta" };

        private const string HtmlHeadMetadata = HtmlControlsNs + ".HtmlHead";

        // HtmlControls types whose constructor takes the rendered tag name
        // (new HtmlHead("head"), new HtmlTableCell("td")/("th")). The
        // ASP.NET compiler emits this argument so the runtime TagName
        // matches the markup. Add a class here when it declares a
        // ctor(string tag) that the framework relies on.
        private static readonly HashSet<string> s_tagNameConstructed =
            new HashSet<string>(StringComparer.Ordinal) { "HtmlHead", "HtmlTableCell" };

        private readonly Dictionary<string, string> _prefixNamespaces;
        // "prefix:tagname" (lower-cased) → generated ASP type metadata name.
        private readonly Dictionary<string, string> _userControls;
        private readonly Func<string, string?>? _resolveCanonical;

        public ControlTypeResolver(
            IReadOnlyDictionary<string, string>? prefixNamespaces = null,
            Func<string, string?>? resolveCanonical = null,
            IReadOnlyDictionary<string, string>? userControls = null)
        {
            _prefixNamespaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (prefixNamespaces != null)
            {
                foreach (KeyValuePair<string, string> pair in prefixNamespaces)
                {
                    _prefixNamespaces[pair.Key] = pair.Value;
                }
            }

            _userControls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (userControls != null)
            {
                foreach (KeyValuePair<string, string> pair in userControls)
                {
                    _userControls[pair.Key] = pair.Value;
                }
            }

            _resolveCanonical = resolveCanonical;
        }

        public ResolvedControl? Resolve(string? prefix, string tagName, string? inputType = null, string? parentMetadata = null)
        {
            if (prefix is null)
            {
                if (string.Equals(tagName, "input", StringComparison.OrdinalIgnoreCase))
                {
                    return ResolveInput(inputType);
                }

                if (s_htmlControls.TryGetValue(tagName, out string? html)
                    && (!s_headOnlyTags.Contains(tagName)
                        || string.Equals(parentMetadata, HtmlHeadMetadata, StringComparison.Ordinal)))
                {
                    string metadata = HtmlControlsNs + "." + html;
                    if (Exists(metadata))
                    {
                        // HtmlHead/HtmlTableCell are constructed with their tag name
                        // (new HtmlHead("head"), new HtmlTableCell("td")); the rest
                        // are parameterless.
                        string? ctorArg = s_tagNameConstructed.Contains(html) ? Quote(tagName) : null;
                        return new ResolvedControl("global::" + metadata, metadata, ctorArg);
                    }
                }

                // Any other runat="server" HTML element — and head-only tags
                // outside a server <head> — fall through to HtmlGenericControl.
                string generic = HtmlControlsNs + ".HtmlGenericControl";
                return new ResolvedControl("global::" + generic, generic, Quote(tagName));
            }

            // A <%@ Register Src %> / web.config src registration: the tag is a
            // user control whose generated ASP type we reference directly. It
            // isn't in the current compilation, so it bypasses the Exists check.
            if (_userControls.TryGetValue(prefix + ":" + tagName, out string? userControl))
            {
                return new ResolvedControl("global::" + userControl, userControl, null, isUserControl: true);
            }

            if (string.Equals(prefix, "asp", StringComparison.OrdinalIgnoreCase))
            {
                return FirstExisting(WebControlsNs + "." + tagName, UiNs + "." + tagName);
            }

            return _prefixNamespaces.TryGetValue(prefix, out string? ns)
                ? FirstExisting(ns + "." + tagName)
                : null;
        }

        private ResolvedControl ResolveInput(string? inputType)
        {
            string html = inputType != null && s_inputControls.TryGetValue(inputType, out string? mapped)
                ? mapped
                : "HtmlInputText";
            string metadata = HtmlControlsNs + "." + html;
            if (!Exists(metadata))
            {
                metadata = HtmlControlsNs + ".HtmlInputText";
            }

            return new ResolvedControl("global::" + metadata, metadata, null);
        }

        private ResolvedControl? FirstExisting(params string[] candidates)
        {
            foreach (string candidate in candidates)
            {
                // Markup tag names are case-insensitive (<asp:label> == <asp:Label>);
                // the resolver returns the canonical, correctly-cased type name.
                if (Canonical(candidate) is { } canonical)
                {
                    return new ResolvedControl("global::" + canonical, canonical, null);
                }
            }

            return null;
        }

        private bool Exists(string metadataName)
        {
            return Canonical(metadataName) != null;
        }

        private string? Canonical(string metadataName)
        {
            return _resolveCanonical is null ? metadataName : _resolveCanonical(metadataName);
        }

        private static string Quote(string value) => CodeLiteral.Escape(value);
    }

    internal readonly struct ResolvedControl
    {
        public ResolvedControl(string typeName, string metadataName, string? constructorArgument, bool isUserControl = false)
        {
            TypeName = typeName;
            MetadataName = metadataName;
            ConstructorArgument = constructorArgument;
            IsUserControl = isUserControl;
        }

        /// <summary>The <c>global::</c>-qualified type used in emitted code.</summary>
        public string TypeName { get; }

        /// <summary>The metadata name (no <c>global::</c>) for symbol lookup.</summary>
        public string MetadataName { get; }

        public string? ConstructorArgument { get; }

        /// <summary>A <c>&lt;%@ Register Src %&gt;</c> user control: its generated
        /// ASP type isn't in the current compilation, so it needs activator-based
        /// construction and an InitializeAsUserControl call.</summary>
        public bool IsUserControl { get; }
    }
}
