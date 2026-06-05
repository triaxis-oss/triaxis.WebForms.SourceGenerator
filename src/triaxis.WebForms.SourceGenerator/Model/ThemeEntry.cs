using System.Collections.Immutable;

namespace triaxis.WebForms.SourceGenerator.Model
{
    /// <summary>
    /// One <c>App_Themes\&lt;name&gt;\</c> subdirectory and the
    /// linked-stylesheet files it contains. Discovered from the
    /// <c>App_Themes\**\*.css</c> AdditionalFiles glob — file paths
    /// declare both the theme name (the directory segment after
    /// <c>App_Themes</c>) and the stylesheets that go in the
    /// <c>LinkedStyleSheets</c> override.
    /// </summary>
    internal sealed record ThemeEntry(
        string Name,
        // ~/App_Themes/&lt;Name&gt;/&lt;file&gt;.css, ordered ordinally — matches the
        // way aspnet_compiler discovers them via Directory.GetFiles.
        ImmutableArray<string> LinkedStyleSheets);
}
