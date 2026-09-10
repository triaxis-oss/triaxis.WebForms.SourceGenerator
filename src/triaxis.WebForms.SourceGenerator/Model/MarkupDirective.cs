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
        public bool Async { get; set; }

        /// <summary>`AsyncTimeout` in seconds — `Page.AsyncTimeout` is a
        /// TimeSpan, the directive is a number.</summary>
        public double? AsyncTimeoutSeconds { get; set; }

        /// <summary>Interfaces from `&lt;%@ Implements %&gt;`, added to the
        /// generated type's base list.</summary>
        public IReadOnlyList<string> Implements { get; set; } = Array.Empty<string>();

        /// <summary>The type `&lt;%@ MasterType %&gt;` narrows `Master` to,
        /// already resolved to a C# type name (a VirtualPath resolves to the
        /// generated `ASP.*` type of that master).</summary>
        public string? MasterType { get; set; }

        /// <summary>The type `&lt;%@ PreviousPageType %&gt;` narrows
        /// `PreviousPage` to, resolved the same way.</summary>
        public string? PreviousPageType { get; set; }

        public IReadOnlyDictionary<string, string> Attributes { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
