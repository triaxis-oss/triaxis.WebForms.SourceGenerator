using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Xml;
using triaxis.WebForms.SourceGenerator.Model;

namespace triaxis.WebForms.SourceGenerator.Parsing
{
    /// <summary>
    /// Reads an <c>App_Browsers\*.browser</c> XML file into a flat list of
    /// <see cref="BrowserEntry"/> values. The runtime grammar is documented
    /// at https://learn.microsoft.com/aspnet/web-forms/overview/older-versions-getting-started/master-pages/browsers-recognized-by-asp-net
    /// — we accept the subset corresponding to ASP.NET 4.x .browser files
    /// (identification / capture / capabilities / controlAdapters).
    /// </summary>
    internal static class BrowserCapabilitiesParser
    {
        public static ImmutableArray<BrowserEntry> Parse(string content, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return ImmutableArray<BrowserEntry>.Empty;
            }

            ImmutableArray<BrowserEntry>.Builder entries = ImmutableArray.CreateBuilder<BrowserEntry>();
            using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                CloseInput = true,
            });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "browser")
                {
                    entries.Add(ReadBrowser(reader, sourcePath));
                }
            }
            return entries.ToImmutable();
        }

        private static BrowserEntry ReadBrowser(XmlReader reader, string sourcePath)
        {
            string? id = reader.GetAttribute("id");
            string? parentId = reader.GetAttribute("parentID");
            string? refId = reader.GetAttribute("refID");

            ImmutableArray<string>.Builder identUaMatches = ImmutableArray.CreateBuilder<string>();
            ImmutableArray<string>.Builder identUaNonMatches = ImmutableArray.CreateBuilder<string>();
            ImmutableArray<(string, string)>.Builder identCapMatches = ImmutableArray.CreateBuilder<(string, string)>();
            ImmutableArray<string>.Builder capturePatterns = ImmutableArray.CreateBuilder<string>();
            ImmutableArray<(string, string)>.Builder capabilities = ImmutableArray.CreateBuilder<(string, string)>();
            ImmutableArray<(string, string)>.Builder adapters = ImmutableArray.CreateBuilder<(string, string)>();

            if (reader.IsEmptyElement)
            {
                return Build();
            }

            // Walk the <browser> children. Order matters within each block:
            // identification is evaluated as a whole, capture as a whole.
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "browser")
                {
                    break;
                }
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.Name)
                {
                    case "identification":
                        ReadIdentificationBlock(reader, identUaMatches, identUaNonMatches, identCapMatches);
                        break;
                    case "capture":
                        ReadCaptureBlock(reader, capturePatterns);
                        break;
                    case "capabilities":
                        ReadCapabilitiesBlock(reader, capabilities);
                        break;
                    case "controlAdapters":
                        ReadControlAdaptersBlock(reader, adapters);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            return Build();

            BrowserEntry Build() => new BrowserEntry(
                id,
                parentId,
                refId,
                identUaMatches.ToImmutable(),
                identUaNonMatches.ToImmutable(),
                identCapMatches.ToImmutable(),
                capturePatterns.ToImmutable(),
                capabilities.ToImmutable(),
                adapters.ToImmutable(),
                sourcePath);
        }

        private static void ReadIdentificationBlock(
            XmlReader reader,
            ImmutableArray<string>.Builder uaMatches,
            ImmutableArray<string>.Builder uaNonMatches,
            ImmutableArray<(string, string)>.Builder capMatches)
        {
            if (reader.IsEmptyElement)
            {
                return;
            }
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "identification")
                {
                    break;
                }
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.Name)
                {
                    case "userAgent":
                    {
                        string? match = reader.GetAttribute("match");
                        string? nonMatch = reader.GetAttribute("nonMatch");
                        if (match != null) uaMatches.Add(match);
                        if (nonMatch != null) uaNonMatches.Add(nonMatch);
                        break;
                    }
                    case "capability":
                    {
                        string? name = reader.GetAttribute("name");
                        string? match = reader.GetAttribute("match");
                        if (name != null && match != null)
                        {
                            capMatches.Add((name, match));
                        }
                        break;
                    }
                }

                if (!reader.IsEmptyElement)
                {
                    reader.Skip();
                }
            }
        }

        private static void ReadCaptureBlock(XmlReader reader, ImmutableArray<string>.Builder patterns)
        {
            if (reader.IsEmptyElement)
            {
                return;
            }
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "capture")
                {
                    break;
                }
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "userAgent")
                {
                    string? match = reader.GetAttribute("match");
                    if (match != null) patterns.Add(match);
                }
            }
        }

        private static void ReadCapabilitiesBlock(XmlReader reader, ImmutableArray<(string, string)>.Builder caps)
        {
            if (reader.IsEmptyElement)
            {
                return;
            }
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "capabilities")
                {
                    break;
                }
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "capability")
                {
                    string? name = reader.GetAttribute("name");
                    string? value = reader.GetAttribute("value");
                    if (name != null && value != null)
                    {
                        caps.Add((name, value));
                    }
                }
            }
        }

        private static void ReadControlAdaptersBlock(XmlReader reader, ImmutableArray<(string, string)>.Builder adapters)
        {
            if (reader.IsEmptyElement)
            {
                return;
            }
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "controlAdapters")
                {
                    break;
                }
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "adapter")
                {
                    string? controlType = reader.GetAttribute("controlType");
                    string? adapterType = reader.GetAttribute("adapterType");
                    if (controlType != null && adapterType != null)
                    {
                        adapters.Add((controlType, adapterType));
                    }
                }
            }
        }
    }
}
