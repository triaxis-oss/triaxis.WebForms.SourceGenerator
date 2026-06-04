using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Compilation;
using triaxis.WebForms.SourceGenerator.Model;

namespace triaxis.WebForms.SourceGenerator.Parsing
{
    /// <summary>A page-local <c>&lt;%@ Register %&gt;</c>: either a tag-prefix →
    /// namespace mapping (Namespace/Assembly form) or a user control
    /// (TagName/Src form).</summary>
    internal sealed class TagRegistration
    {
        public string Prefix { get; set; } = string.Empty;
        public string? TagName { get; set; }
        public string? Src { get; set; }
        public string? Namespace { get; set; }
    }

    internal sealed class ParsedMarkup
    {
        public MarkupDirective? Directive { get; set; }
        public IReadOnlyList<string> Imports { get; set; } = Array.Empty<string>();
        public IReadOnlyList<TagRegistration> Registrations { get; set; } = Array.Empty<TagRegistration>();
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Runs the vendored Mono front-end over one markup file and folds its
    /// event stream into the model the emitter consumes. This slice captures
    /// the leading page/control/master directive; control-tree folding is
    /// layered on next.
    /// </summary>
    internal static class MarkupParserDriver
    {
        public static ParsedMarkup Parse(string filename, TextReader input)
        {
            var errors = new List<string>();
            var imports = new List<string>();
            var registrations = new List<TagRegistration>();
            MarkupDirective? directive = null;

            var parser = new AspParser(filename, input);
            parser.Error += (location, message) => errors.Add($"L{location.BeginLine}: {message}");
            parser.TagParsed += (location, tagType, id, attributes) =>
            {
                if (tagType != TagType.Directive)
                {
                    return;
                }

                if (string.Equals(id, "Import", StringComparison.OrdinalIgnoreCase)
                    && attributes?["namespace"] is string ns && !string.IsNullOrWhiteSpace(ns))
                {
                    imports.Add(ns);
                    return;
                }

                if (string.Equals(id, "Register", StringComparison.OrdinalIgnoreCase)
                    && attributes?["tagprefix"] is string prefix && !string.IsNullOrWhiteSpace(prefix))
                {
                    registrations.Add(new TagRegistration
                    {
                        Prefix = prefix,
                        TagName = attributes["tagname"] as string,
                        Src = attributes["src"] as string,
                        Namespace = attributes["namespace"] as string,
                    });
                    return;
                }

                if (directive == null && attributes != null && TryDirectiveKind(id, out MarkupKind kind))
                {
                    directive = BuildDirective(kind, attributes);
                }
            };

            parser.Parse();
            return new ParsedMarkup { Directive = directive, Imports = imports, Registrations = registrations, Errors = errors };
        }

        private static bool TryDirectiveKind(string id, out MarkupKind kind)
        {
            switch (id?.ToLowerInvariant())
            {
                case "page": kind = MarkupKind.Page; return true;
                case "control": kind = MarkupKind.Control; return true;
                case "master": kind = MarkupKind.Master; return true;
                default: kind = default; return false;
            }
        }

        private static MarkupDirective BuildDirective(MarkupKind kind, TagAttributes attributes)
        {
            Dictionary<string, string> map = ToMap(attributes);
            var directive = new MarkupDirective
            {
                Kind = kind,
                Attributes = map,
            };

            if (map.TryGetValue("Inherits", out string? inherits)) { directive.Inherits = inherits; }
            if (map.TryGetValue("ClassName", out string? className)) { directive.ClassName = className; }
            if (map.TryGetValue("Title", out string? title)) { directive.Title = title; }
            if (map.TryGetValue("MasterPageFile", out string? master)) { directive.MasterPageFile = master; }
            if (map.TryGetValue("Language", out string? language) && !string.IsNullOrWhiteSpace(language)) { directive.Language = language; }
            if (map.TryGetValue("AutoEventWireup", out string? autoEvent)) { directive.AutoEventWireup = ParseBool(autoEvent, defaultValue: true); }
            if (map.TryGetValue("EnableSessionState", out string? session))
            {
                directive.RequiresSessionState = !string.Equals(session, "False", StringComparison.OrdinalIgnoreCase);
            }

            return directive;
        }

        private static Dictionary<string, string> ToMap(TagAttributes attributes)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (attributes == null)
            {
                return map;
            }

            foreach (object key in attributes.Keys)
            {
                if (key is string name)
                {
                    map[name] = attributes[key] as string ?? string.Empty;
                }
            }

            return map;
        }

        private static bool ParseBool(string? value, bool defaultValue)
        {
            return bool.TryParse(value, out bool result) ? result : defaultValue;
        }
    }
}
