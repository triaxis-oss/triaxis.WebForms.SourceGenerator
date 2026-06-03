using System;
using System.Collections.Generic;

namespace triaxis.WebForms.SourceGenerator.Model
{
    internal enum MarkupKind
    {
        Page,
        Control,
        Master,
    }

    /// <summary>
    /// The page/control/master `&lt;%@ ... %&gt;` directive, merged into the
    /// fields the frame emitter needs. Attribute lookups are case-insensitive
    /// because markup authors are inconsistent (`Inherits` vs `inherits`).
    /// </summary>
    internal sealed class MarkupDirective
    {
        public MarkupKind Kind { get; set; } = MarkupKind.Page;
        public string? Inherits { get; set; }
        public string? ClassName { get; set; }
        public string? Title { get; set; }
        public string? MasterPageFile { get; set; }
        public string Language { get; set; } = "C#";
        public bool AutoEventWireup { get; set; } = true;
        public bool RequiresSessionState { get; set; } = true;
        public IReadOnlyDictionary<string, string> Attributes { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
