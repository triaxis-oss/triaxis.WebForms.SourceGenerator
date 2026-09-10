using System;
using System.Collections.Generic;

namespace triaxis.WebForms.SourceGenerator.Model
{
    /// <summary>
    /// The registry of what the generator actually does with a page/control/master
    /// directive attribute. Emission reads attributes from a handful of
    /// hand-written tables, so an attribute nobody listed is parsed and then
    /// dropped — the markup compiles clean and quietly loses behavior
    /// (<c>Async="true"</c> lived that way until it was implemented). Everything
    /// not accounted for here is reported as TWF004.
    /// </summary>
    /// <remarks>
    /// When you teach the generator a new attribute, add it to
    /// <see cref="s_honored"/> against the kinds whose emission reads it —
    /// otherwise the build warns about an attribute that now works.
    /// </remarks>
    internal static class DirectiveAttributes
    {
        private static readonly MarkupKind[] AllKinds =
            { MarkupKind.Page, MarkupKind.Control, MarkupKind.Master };

        private static readonly MarkupKind[] PageOnly = { MarkupKind.Page };

        // Attribute → the markup kinds whose emission acts on it. Page-only
        // entries stay Page-only: EmitPageDefaults and the Page frame members
        // don't run for a UserControl or MasterPage, so the same attribute on
        // a .ascx really is dropped and really should warn.
        private static readonly (string Name, MarkupKind[] Kinds)[] s_honored =
        {
            ("Inherits",                         AllKinds),
            ("AutoEventWireup",                  AllKinds),
            ("MasterPageFile",                   new[] { MarkupKind.Page, MarkupKind.Master }),
            ("Title",                            PageOnly),
            ("Async",                            PageOnly),
            ("AsyncTimeout",                     PageOnly),
            ("EnableSessionState",               PageOnly),
            ("ValidateRequest",                  PageOnly),
            ("MaintainScrollPositionOnPostBack", PageOnly),
            ("EnableEventValidation",            PageOnly),
            ("StyleSheetTheme",                  PageOnly),
            ("Theme",                            PageOnly),
            ("EnableViewState",                  PageOnly),
        };

        // Attributes that steer the *old* compile model — where the source
        // lived, how it was compiled, what the designer showed. This generator
        // compiles the markup into the host assembly instead, so ignoring them
        // is the design, not a gap, and warning about them would be noise.
        private static readonly HashSet<string> s_notApplicable =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CodeBehind", "CodeFile", "CodeFileBaseClass", "Src",
                "CompilerOptions", "WarningLevel", "Debug", "Explicit", "Strict",
                "LinePragmas", "Description", "TargetSchema", "CompileWith",
                "Language",
            };

        /// <summary>
        /// The attributes present on <paramref name="directive"/> that this
        /// generator does not act on — unimplemented ones, ones valid only for
        /// another markup kind, and outright typos.
        /// </summary>
        public static IEnumerable<string> Unhonored(MarkupDirective directive)
        {
            foreach (KeyValuePair<string, string> attribute in directive.Attributes)
            {
                if (s_notApplicable.Contains(attribute.Key))
                {
                    continue;
                }

                if (!IsHonored(attribute.Key, directive.Kind))
                {
                    yield return attribute.Key;
                }
            }

            // Language is otherwise not applicable — the generator emits C#
            // whatever the markup asks for, which only matters when it asks
            // for something else.
            if (directive.Attributes.TryGetValue("Language", out string? language) && !IsCSharp(language))
            {
                yield return "Language";
            }
        }

        private static bool IsHonored(string name, MarkupKind kind)
        {
            foreach ((string honored, MarkupKind[] kinds) in s_honored)
            {
                if (string.Equals(honored, name, StringComparison.OrdinalIgnoreCase))
                {
                    return Array.IndexOf(kinds, kind) >= 0;
                }
            }

            return false;
        }

        private static bool IsCSharp(string language)
        {
            return string.IsNullOrWhiteSpace(language)
                || language.Equals("C#", StringComparison.OrdinalIgnoreCase)
                || language.Equals("CS", StringComparison.OrdinalIgnoreCase)
                || language.Equals("CSharp", StringComparison.OrdinalIgnoreCase);
        }
    }
}
