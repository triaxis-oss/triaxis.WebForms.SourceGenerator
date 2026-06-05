using System.Collections.Immutable;

namespace triaxis.WebForms.SourceGenerator.Model
{
    /// <summary>
    /// One <c>App_Themes\&lt;name&gt;\</c> subdirectory and the file
    /// content it exposes to the theme emitter — linked stylesheets
    /// (<c>LinkedStyleSheets</c> override) plus the <c>.skin</c> files
    /// that get compiled into per-skin <c>ControlSkinDelegate</c>
    /// methods registered in <c>__controlSkins</c>.
    /// </summary>
    internal sealed record ThemeEntry(
        string Name,
        // ~/App_Themes/<Name>/<file>.css, ordered ordinally — matches the
        // way aspnet_compiler discovers them via Directory.GetFiles.
        ImmutableArray<string> LinkedStyleSheets,
        // Each entry is the verbatim content of one .skin file plus the
        // virtual path the parser reports diagnostics under, ordered by
        // path so the per-skin __BuildControl numbering is deterministic.
        ImmutableArray<(string VirtualPath, string Content)> SkinFiles);
}
