namespace triaxis.WebForms.SourceGenerator.Emit
{
    /// <summary>
    /// Centralizes the framework type identifiers the generator pivots on.
    /// </summary>
    /// <remarks>
    /// Every constant here is a place the generator hardcodes a System.Web
    /// identifier because the corresponding type either (a) lives in a
    /// Framework-only assembly the analyzer process can't load, or (b)
    /// participates in the <c>aspnet_compiler</c> codegen contract in a way
    /// that the symbol metadata alone doesn't surface (auto-promotion,
    /// whitespace-discard, ContentPlaceHolder registration, …).
    /// <para>
    /// To add a new entry: pick the right group based on what the generator
    /// does with the identifier (probe, route, gate), write a one-sentence
    /// XML-doc explaining what about the framework forces the hardcoding,
    /// then add the constant. Every consumer in the codebase routes through
    /// this file, so a single edit suffices.
    /// </para>
    /// </remarks>
    internal static class WebFormsContract
    {
        /// <summary>
        /// The marker interface <c>aspnet_compiler</c> probes to decide
        /// whether an attribute that doesn't match a typed property can be
        /// routed through <c>SetAttribute(string, string)</c>. Hardcoded
        /// because System.Web isn't loadable into the analyzer process.
        /// </summary>
        public const string AttributeAccessor = "System.Web.UI.IAttributeAccessor";

        /// <summary>
        /// The runtime contract for a deferred control-tree slot. Any
        /// property typed as <c>ITemplate</c> (or whose type implements it)
        /// is emitted as a <c>CompiledTemplateBuilder</c>, not assigned as a
        /// plain value.
        /// </summary>
        public const string Template = "System.Web.UI.ITemplate";

        /// <summary>
        /// A master page's named slot. The generator hardcodes the metadata
        /// name so it can register each instance's <c>ID</c> with the master
        /// at codegen time, matching what <c>aspnet_compiler</c> emits.
        /// </summary>
        public const string ContentPlaceHolder = "System.Web.UI.WebControls.ContentPlaceHolder";

        /// <summary>
        /// <c>HtmlHead</c> has parser-level peculiarities not visible on its
        /// symbol: it auto-promotes <c>title</c>/<c>meta</c>/<c>link</c>/
        /// <c>base</c> children and it drops whitespace-only literal runs
        /// (<c>HtmlHeadBuilder.OnAppendLiteralString</c>). The generator
        /// reproduces both without loading System.Web.
        /// </summary>
        public const string HtmlHead = "System.Web.UI.HtmlControls.HtmlHead";

        /// <summary>
        /// Returns <c>true</c> when whitespace-only literal runs should be
        /// dropped inside a server container of the given metadata name —
        /// mirrors <c>HtmlHeadBuilder</c>'s parser behavior. Add other
        /// containers here if the framework exposes any (none today).
        /// </summary>
        public static bool DiscardsWhitespaceLiterals(string parentMetadata)
        {
            return parentMetadata == HtmlHead;
        }
    }
}
