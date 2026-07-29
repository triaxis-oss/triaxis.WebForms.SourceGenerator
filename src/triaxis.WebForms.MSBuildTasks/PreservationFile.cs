using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace triaxis.WebForms.MSBuildTasks
{
    /// <summary>
    /// Pure helpers for emitting ASP.NET <c>.compiled</c> preservation files —
    /// the lean sidecars that route <c>BuildManager</c> to a prebuilt type in a
    /// non-updatable precompiled app. Kept free of MSBuild types so the format
    /// logic (filename hash, directive parsing, type/assembly resolution) is
    /// unit-testable without a build host.
    /// </summary>
    internal static class PreservationFile
    {
        // BuildResultTypeCode values the runtime reads out of a preserve element.
        // Pages and themes use 3 (BuildResultCompiledTemplateType); a prebuilt-type
        // handler (.asmx/.ashx) uses 2 (BuildResultCompiledType); a WCF .svc uses 5
        // (BuildResultCustomString) and carries its activation data in customString.
        public const string CompiledType = "2";
        public const string CompiledTemplateType = "3";
        public const string CustomString = "5";

        // A standard .asmx/.ashx/.svc directive names its prebuilt type with a
        // single quoted attribute; the runtime never compiles the file, it binds
        // straight to that type.
        public static string? ExtractAttribute(string markup, string attribute)
        {
            Match m = Regex.Match(markup, "\\b" + Regex.Escape(attribute) + "\\s*=\\s*\"([^\"]+)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return m.Success ? m.Groups[1].Value : null;
        }

        // A directive type can be assembly-qualified ("Ns.Type, Some.Assembly");
        // the explicit assembly then needs no output scan to resolve. Splits on
        // the first comma, leaving the bare type aspnet_compiler writes.
        public static (string Type, string? Assembly) SplitTypeName(string value)
        {
            int comma = value.IndexOf(',');
            return comma < 0
                ? (value.Trim(), null)
                : (value.Substring(0, comma).Trim(), value.Substring(comma + 1).Trim());
        }

        // The App_Themes\<name>\ folder a virtual path belongs to, which is both
        // the theme's name and the identifier its generated shell is named after.
        // Null for a path outside a theme folder (a file sitting directly in
        // App_Themes\ belongs to no theme) — same rule the generator applies when
        // it decides which shells to emit, so the two stay in lockstep.
        public static string? ThemeName(string virtualPath)
        {
            const string prefix = "/App_Themes/";
            if (!virtualPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            int end = virtualPath.IndexOf('/', prefix.Length);
            return end < 0 ? null : virtualPath.Substring(prefix.Length, end - prefix.Length);
        }

        public static string PreserveType(string resultType, string virtualPath, string assembly, string type) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<preserve resultType=\"" + resultType + "\" virtualPath=\"" + virtualPath + "\" assembly=\"" +
            assembly + "\" type=\"" + type + "\" />\r\n";

        public static string PreserveCustomString(string virtualPath, string customString) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<preserve resultType=\"" + CustomString + "\" virtualPath=\"" + virtualPath + "\" customString=\"" +
            customString + "\" />\r\n";

        // The activation payload System.ServiceModel.Activation's HostingManager
        // splits out of a precompiled .svc: app-relative path | factory (empty →
        // ServiceHostFactory) | service type | the assembly that defines it.
        // ServiceHostFactory.CreateServiceHost reads the trailing assemblies only
        // to resolve the simple-named service type (Assembly.Load → GetType), so
        // the one defining it is all that's needed — aspnet_compiler lists the
        // whole reference closure, but every entry past the defining one is loaded
        // and discarded.
        public static string ServiceCustomString(string virtualPath, string factory, string service, string assembly) =>
            "~" + virtualPath + "|" + factory + "|" + service + "|" + assembly;

        // The cache-key filename BuildManager derives from a virtual path:
        // "<file>.<hash-of-app-relative-dir>.compiled". GetStringHashCode is
        // System.Web.Util.StringUtil.GetStringHashCode ported byte-for-byte so
        // the names match what BuildManager probes by.
        public static string Name(string virtualPath)
        {
            string appRel = ("~" + (virtualPath.StartsWith("/") ? virtualPath : "/" + virtualPath))
                .ToLowerInvariant().TrimEnd('/');
            int slash = appRel.LastIndexOf('/');
            string name = appRel.Substring(slash + 1);
            string dir = slash <= 0 ? "/" : appRel.Substring(0, slash);
            return name + "." + GetStringHashCode(dir).ToString("x", CultureInfo.InvariantCulture) + ".compiled";
        }

        /// <summary>
        /// Maps each wanted full type name to the simple name of the assembly that
        /// defines it, reading type metadata from the build outputs — the bind
        /// aspnet_compiler does for a prebuilt .asmx/.ashx handler. Types not found
        /// in any assembly are absent from the result.
        /// </summary>
        public static Dictionary<string, string> ResolveAssemblies(
            ISet<string> wantedFullNames, IEnumerable<string> assemblyPaths)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (wantedFullNames.Count == 0)
            {
                return result;
            }

            // Cheap pre-filter: only build a type's full name once its innermost
            // identifier is one we're after. A nested name's innermost segment
            // sits after the last '.' or '+'.
            var wantedSimpleNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (string full in wantedFullNames)
            {
                int sep = full.LastIndexOfAny(s_nameSeparators);
                wantedSimpleNames.Add(sep < 0 ? full : full.Substring(sep + 1));
            }

            // Sorted so a type defined in more than one output assembly binds to a
            // reproducible one rather than to filesystem enumeration order.
            foreach (string path in assemblyPaths.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (result.Count == wantedFullNames.Count)
                {
                    break;
                }

                try
                {
                    using var stream = File.OpenRead(path);
                    using var pe = new PEReader(stream);
                    if (!pe.HasMetadata)
                    {
                        continue;
                    }

                    MetadataReader mr = pe.GetMetadataReader();
                    string? assemblyName = null;
                    foreach (TypeDefinitionHandle handle in mr.TypeDefinitions)
                    {
                        TypeDefinition td = mr.GetTypeDefinition(handle);
                        if (!wantedSimpleNames.Contains(mr.GetString(td.Name)))
                        {
                            continue;
                        }

                        string fullName = FullName(mr, td);
                        if (wantedFullNames.Contains(fullName) && !result.ContainsKey(fullName))
                        {
                            assemblyName ??= mr.GetString(mr.GetAssemblyDefinition().Name);
                            result[fullName] = assemblyName;
                        }
                    }
                }
                catch (Exception)
                {
                    // Unreadable or non-managed file in the output dir — skip it;
                    // a genuinely missing handler type surfaces to the caller.
                }
            }

            return result;
        }

        private static readonly char[] s_nameSeparators = { '.', '+' };

        // Metadata-row full name, walking enclosing types so a nested handler
        // class reads back as "Ns.Outer+Inner" (its TypeDefinition carries only
        // the innermost identifier and an empty namespace).
        private static string FullName(MetadataReader mr, TypeDefinition td)
        {
            string name = mr.GetString(td.Name);
            TypeDefinitionHandle declaring = td.GetDeclaringType();
            if (declaring.IsNil)
            {
                string ns = mr.GetString(td.Namespace);
                return ns.Length == 0 ? name : ns + "." + name;
            }

            return FullName(mr, mr.GetTypeDefinition(declaring)) + "+" + name;
        }

        // System.Web.Util.StringUtil.GetStringHashCode, ported exactly so the
        // filenames match the cache key BuildManager probes by.
        private static int GetStringHashCode(string s)
        {
            int Ch(int i) => i < s.Length ? s[i] : 0;
            int hash1 = (5381 << 16) + 5381;
            int hash2 = hash1;
            int pos = 0, len = s.Length;
            while (len > 0)
            {
                hash1 = ((hash1 << 5) + hash1 + (hash1 >> 27)) ^ (Ch(pos) | (Ch(pos + 1) << 16));
                if (len <= 2)
                {
                    break;
                }
                hash2 = ((hash2 << 5) + hash2 + (hash2 >> 27)) ^ (Ch(pos + 2) | (Ch(pos + 3) << 16));
                pos += 4;
                len -= 4;
            }
            return hash1 + (hash2 * 1566083941);
        }
    }
}
