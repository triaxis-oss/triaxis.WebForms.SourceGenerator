using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using triaxis.WebForms.SourceGenerator.Emit;

namespace triaxis.WebForms.SourceGenerator.Parsing
{
    /// <summary>
    /// Extracts the four page-level inputs the generator pulls out of a
    /// project's <c>web.config</c> files — tag-prefix → namespace
    /// mappings, page-wide imports, <c>&lt;pages&gt;</c> attribute defaults,
    /// and registered user controls — in a single <see cref="XDocument"/>
    /// parse per file. The previous arrangement Selected on the same XML
    /// from four independent incremental pipelines and parsed the document
    /// four times for every consumer build.
    /// </summary>
    /// <remarks>
    /// To add a new <c>&lt;system.web&gt;</c> binding the generator should
    /// observe (e.g. <c>&lt;httpRuntime&gt;</c> properties), extend
    /// <see cref="WebConfigBindings"/> with the new field and pull it out
    /// in <see cref="Parse"/> / merge it in <see cref="Merge"/>. Every
    /// downstream consumer reads through the bindings record.
    /// </remarks>
    internal static class WebConfigParser
    {
        public static WebConfigBindings Parse(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return WebConfigBindings.Empty;
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(xml);
            }
            catch (XmlException)
            {
                // Malformed web.config shouldn't crash the generator; callers
                // already fall back to verbatim literals for anything we
                // couldn't bind.
                return WebConfigBindings.Empty;
            }

            return new WebConfigBindings(
                Prefixes: ExtractPrefixes(document),
                PageNamespaces: ExtractPageNamespaces(document),
                PageDefaults: ExtractPageDefaults(document),
                UserControls: ExtractUserControls(document));
        }

        public static WebConfigBindings Merge(ImmutableArray<WebConfigBindings> parsed)
        {
            var prefixes = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            var pageDefaults = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            var userControls = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (WebConfigBindings p in parsed)
            {
                foreach (KeyValuePair<string, string> pair in p.Prefixes) prefixes[pair.Key] = pair.Value;
                foreach (string ns in p.PageNamespaces) namespaces.Add(ns);
                foreach (KeyValuePair<string, string> pair in p.PageDefaults) pageDefaults[pair.Key] = pair.Value;
                foreach (KeyValuePair<string, string> pair in p.UserControls) userControls[pair.Key] = pair.Value;
            }

            return new WebConfigBindings(
                prefixes.ToImmutable(),
                namespaces.ToImmutableArray(),
                pageDefaults.ToImmutable(),
                userControls.ToImmutable());
        }

        // <controls><add tagPrefix namespace> → prefix → ns
        private static ImmutableDictionary<string, string> ExtractPrefixes(XDocument document)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement add in ControlAdds(document))
            {
                string? prefix = add.Attribute("tagPrefix")?.Value;
                string? ns = add.Attribute("namespace")?.Value;
                if (!string.IsNullOrWhiteSpace(prefix) && !string.IsNullOrWhiteSpace(ns))
                {
                    builder[prefix!] = ns!;
                }
            }
            return builder.ToImmutable();
        }

        // <controls><add tagPrefix tagName src> → "prefix:tagName" → ASP type
        // metadata name derived from src.
        private static ImmutableDictionary<string, string> ExtractUserControls(XDocument document)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement add in ControlAdds(document))
            {
                string? prefix = add.Attribute("tagPrefix")?.Value;
                string? tagName = add.Attribute("tagName")?.Value;
                string? src = add.Attribute("src")?.Value;
                if (!string.IsNullOrWhiteSpace(prefix) && !string.IsNullOrWhiteSpace(tagName) && !string.IsNullOrWhiteSpace(src))
                {
                    builder[prefix + ":" + tagName] = UserControlMetadata(src!, "/");
                }
            }
            return builder.ToImmutable();
        }

        // <pages><namespaces><add namespace> — the page-wide imports the
        // markup's inline code expects.
        private static ImmutableArray<string> ExtractPageNamespaces(XDocument document)
        {
            return document.Descendants()
                .Where(e => e.Name.LocalName == "namespaces")
                .Elements()
                .Where(e => e.Name.LocalName == "add")
                .Select(e => e.Attribute("namespace")?.Value)
                .Where(ns => !string.IsNullOrWhiteSpace(ns))
                .Select(ns => ns!)
                .ToImmutableArray();
        }

        // The <pages> element's own attributes (theme, enableEventValidation, …).
        private static ImmutableDictionary<string, string> ExtractPageDefaults(XDocument document)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            XElement? pages = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "pages");
            if (pages != null)
            {
                foreach (XAttribute attribute in pages.Attributes())
                {
                    builder[attribute.Name.LocalName] = attribute.Value;
                }
            }
            return builder.ToImmutable();
        }

        private static IEnumerable<XElement> ControlAdds(XDocument document) =>
            document.Descendants()
                .Where(e => e.Name.LocalName == "controls")
                .Elements()
                .Where(e => e.Name.LocalName == "add");

        // src → generated ASP type name. Resolves "~/" against the app root
        // and relative paths against the supplied page directory.
        public static string UserControlMetadata(string src, string pageDirectory)
        {
            string path = src.Replace('\\', '/');
            string virtualPath =
                path.StartsWith("~/", StringComparison.Ordinal) ? path.Substring(1)
                : path.StartsWith("/", StringComparison.Ordinal) ? path
                : pageDirectory.TrimEnd('/') + "/" + path;

            virtualPath = NormalizeDotSegments(virtualPath);
            return GeneratedNaming.Namespace + "." + GeneratedNaming.TypeNameFromVirtualPath(virtualPath);
        }

        private static string NormalizeDotSegments(string path)
        {
            var segments = new List<string>();
            foreach (string segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }
                }
                else
                {
                    segments.Add(segment);
                }
            }

            return "/" + string.Join("/", segments);
        }
    }

    /// <summary>
    /// The four page-level inputs the generator pulls out of web.config:
    /// tag-prefix mappings, page-wide imports, <c>&lt;pages&gt;</c> attribute
    /// defaults, and registered user controls. Returned by both the
    /// per-file parser and the merge across files.
    /// </summary>
    internal readonly record struct WebConfigBindings(
        ImmutableDictionary<string, string> Prefixes,
        ImmutableArray<string> PageNamespaces,
        ImmutableDictionary<string, string> PageDefaults,
        ImmutableDictionary<string, string> UserControls)
    {
        public static WebConfigBindings Empty { get; } = new(
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            ImmutableArray<string>.Empty,
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));
    }
}
