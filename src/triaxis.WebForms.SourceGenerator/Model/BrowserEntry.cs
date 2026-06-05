using System.Collections.Immutable;

namespace triaxis.WebForms.SourceGenerator.Model
{
    /// <summary>
    /// One <c>&lt;browser&gt;</c> definition parsed from an
    /// <c>App_Browsers\*.browser</c> file. Either <see cref="Id"/> +
    /// <see cref="ParentId"/> identify a new browser hung off an existing
    /// one in the runtime's built-in tree, or <see cref="RefId"/> refines
    /// an existing browser (adding control-adapters or capabilities to it
    /// without introducing a new node).
    /// </summary>
    internal sealed record BrowserEntry(
        string? Id,
        string? ParentId,
        string? RefId,
        // userAgent identification probes — every Match must pass and every
        // NonMatch must fail for the entry's Process method to claim the
        // request. Match patterns may declare named capture groups that
        // capability values reference via ${name} substitution.
        ImmutableArray<string> IdentificationUserAgentMatches,
        ImmutableArray<string> IdentificationUserAgentNonMatches,
        // identification probes against existing capabilities. The first
        // string is the capability name; the second is the regex that
        // capability's value must match for the browser to claim.
        ImmutableArray<(string Capability, string Pattern)> IdentificationCapabilityMatches,
        // additional capture-group regex runs that happen after the
        // identification check succeeds — they don't gate the match, they
        // just populate more named groups for capability substitution.
        ImmutableArray<string> CaptureUserAgentPatterns,
        // (name, value) entries set on the request's capability dictionary.
        // Values may contain ${captureGroup} references that get
        // substituted via the RegexWorker established by identification +
        // capture.
        ImmutableArray<(string Name, string Value)> Capabilities,
        // (controlType, adapterType) pairs registered into the request's
        // adapters table — runtime then routes the controlType's rendering
        // through the adapterType.
        ImmutableArray<(string ControlType, string AdapterType)> ControlAdapters,
        // Source file path, kept for diagnostic reporting.
        string SourcePath);
}
