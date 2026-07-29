using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace triaxis.WebForms.MSBuildTasks
{
    /// <summary>
    /// Emits the lean <c>.compiled</c> sidecar files that point ASP.NET's
    /// <c>BuildManager</c> at prebuilt types in a non-updatable precompiled app,
    /// replacing the <c>aspnet_compiler</c> / <c>aspnet_merge</c> precompile:
    /// one per markup file (the Roslyn-generated <c>ASP.&lt;name&gt;_aspx</c>
    /// page type) and one per <c>.asmx</c>/<c>.ashx</c>/<c>.svc</c> handler (the
    /// prebuilt type named in its directive).
    /// </summary>
    /// <remarks>
    /// The runtime (precompiled, non-updatable) reads only
    /// <c>resultType</c> plus the type/assembly (or, for <c>.svc</c>,
    /// <c>customString</c>) — hashes and <c>filedeps</c> are ignored — so each
    /// file is a short template. The filename is the cache key
    /// <c>BuildManager</c> derives from the virtual path
    /// (<c>System.Web.Util.StringUtil.GetStringHashCode</c> of the app-relative
    /// dir), ported in <see cref="PreservationFile.Name"/>.
    /// </remarks>
    public sealed class GenerateWebFormsCompiledFiles : Task
    {
        [Required] public string ProjectDir { get; set; } = string.Empty;
        [Required] public string OutputDir { get; set; } = string.Empty;
        [Required] public string AssemblyName { get; set; } = string.Empty;
        [Required] public ITaskItem[] MarkupFiles { get; set; } = Array.Empty<ITaskItem>();
        // App_Browsers\*.browser inputs. When non-empty, emit a single
        // __browserCapabilitiesCompiler.compiled sidecar pointing BuildManager
        // at the source-generated ASP.ApplicationBrowserCapabilitiesFactory so
        // the runtime resolves it at app start; without this file the factory
        // class is present but never wired and .browser entries silently
        // don't apply.
        public ITaskItem[] BrowserFiles { get; set; } = Array.Empty<ITaskItem>();
        // App_Themes\<name>\ content (.css / .skin) — the same set the generator
        // folds into one ASP.<name> PageTheme shell per folder. Each distinct
        // folder needs a Theme_<name>.compiled sidecar; without it the runtime
        // falls back to compiling the theme directory on first request, which a
        // non-updatable precompiled app can't do.
        public ITaskItem[] ThemeFiles { get; set; } = Array.Empty<ITaskItem>();
        // .asmx / .ashx / .svc handlers. Unlike markup these aren't source-
        // generated into page types — each directive names a prebuilt type a
        // non-updatable precompiled app still needs a .compiled sidecar to reach.
        public ITaskItem[] HandlerFiles { get; set; } = Array.Empty<ITaskItem>();
        // Build outputs scanned to bind each handler to the assembly that defines
        // its type — the host assembly for in-project code-behind, a referenced
        // assembly otherwise.
        public ITaskItem[] ReferenceAssemblies { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            Directory.CreateDirectory(OutputDir);
            string root = Path.GetFullPath(ProjectDir).Replace('\\', '/').TrimEnd('/') + "/";
            int count = 0;

            foreach (ITaskItem item in MarkupFiles)
            {
                string vpath = ToVirtualPath(item, root);
                bool isGlobalAsax = string.Equals(Path.GetFileName(vpath), "global.asax", StringComparison.OrdinalIgnoreCase);
                if (isGlobalAsax)
                {
                    // global.asax is looked up by BuildManager.GlobalAsaxAssemblyName
                    // ("App_global.asax"), not the page cache-key hash, and resolves
                    // to result type 8 (BuildResultCompiledGlobalAsaxType).
                    vpath = "/global.asax";
                }

                string resultType = isGlobalAsax ? "8" : PreservationFile.CompiledTemplateType;
                string typeName = isGlobalAsax ? "ASP._global_asax" : "ASP." + Mangle(vpath);
                string fileName = isGlobalAsax ? "App_global.asax.compiled" : PreservationFile.Name(vpath);
                File.WriteAllText(Path.Combine(OutputDir, fileName),
                    PreservationFile.PreserveType(resultType, vpath, AssemblyName, typeName));
                count++;
            }

            count += EmitHandlerCompiled(root);
            count += EmitThemeCompiled(root);

            if (BrowserFiles.Length > 0)
            {
                // The browser-capabilities factory isn't cached under a
                // path-derived key like a page — BrowserCapabilitiesCompiler
                // asks BuildManager for the fixed special cache key
                // "__browserCapabilitiesCompiler", so the sidecar carries that
                // name verbatim (no hash suffix) and a plain
                // BuildResultCompiledType (2). Virtual path keeps the trailing
                // slash aspnet_compiler writes for a directory result.
                File.WriteAllText(Path.Combine(OutputDir, "__browserCapabilitiesCompiler.compiled"),
                    PreservationFile.PreserveType(PreservationFile.CompiledType, "/App_Browsers/", AssemblyName, "ASP.ApplicationBrowserCapabilitiesFactory"));
                count++;
            }

            Log.LogMessage(MessageImportance.High, "Generated " + count + " .compiled files in " + OutputDir);
            // A handler that couldn't be bound logged an error (TWF004): fail the
            // build rather than ship a deployment that 500s at first request.
            return !Log.HasLoggedErrors;
        }

        // One sidecar per App_Themes\<name>\ folder. Like the browser factory
        // a theme isn't cached under a path-derived key — ThemeDirectoryCompiler
        // asks BuildManager for "Theme_<name>", the folder name verbatim — so the
        // file carries that name with no hash suffix. The type is the generator's
        // ASP.<name> shell, and the virtual path keeps the trailing slash
        // aspnet_compiler writes for a directory result.
        private int EmitThemeCompiled(string root)
        {
            var themes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (ITaskItem item in ThemeFiles)
            {
                string? theme = PreservationFile.ThemeName(ToVirtualPath(item, root));
                if (theme != null)
                {
                    themes.Add(theme);
                }
            }

            foreach (string theme in themes)
            {
                File.WriteAllText(Path.Combine(OutputDir, "Theme_" + theme + ".compiled"),
                    PreservationFile.PreserveType(PreservationFile.CompiledTemplateType,
                        "/App_Themes/" + theme + "/", AssemblyName, "ASP." + theme));
            }

            return themes.Count;
        }

        private int EmitHandlerCompiled(string root)
        {
            if (HandlerFiles.Length == 0)
            {
                return 0;
            }

            // Parse every directive up front so the output assemblies are scanned
            // once for the whole set rather than per handler.
            var typeHandlers = new List<(string VirtualPath, string Class)>();
            var serviceHandlers = new List<(string VirtualPath, string Service, string Factory)>();
            foreach (ITaskItem item in HandlerFiles)
            {
                string vpath = ToVirtualPath(item, root);
                string markup = File.ReadAllText(item.GetMetadata("FullPath"));

                if (string.Equals(Path.GetExtension(vpath), ".svc", StringComparison.OrdinalIgnoreCase))
                {
                    string? service = PreservationFile.ExtractAttribute(markup, "Service");
                    if (service is null)
                    {
                        Log.LogError(null, "TWF004", null, null, 0, 0, 0, 0,
                            "WebForms .svc handler '{0}' has no Service directive, so it cannot be precompiled. Add the ServiceHost Service attribute, or exclude the file via WebFormsGenHandlerExclude.", vpath);
                        continue;
                    }

                    serviceHandlers.Add((vpath, service, PreservationFile.ExtractAttribute(markup, "Factory") ?? string.Empty));
                }
                else
                {
                    string? className = PreservationFile.ExtractAttribute(markup, "Class");
                    if (className is null)
                    {
                        Log.LogError(null, "TWF004", null, null, 0, 0, 0, 0,
                            "WebForms handler '{0}' has no Class directive, so it cannot be precompiled. Add the directive's Class attribute, or exclude the file via WebFormsGenHandlerExclude.", vpath);
                        continue;
                    }

                    typeHandlers.Add((vpath, className));
                }
            }

            // Every handler binds to a prebuilt type; resolve them all in one scan
            // of the build outputs. Only bare "Ns.Type" names need it — an
            // assembly-qualified directive already states where its type lives.
            var wanted = new HashSet<string>(
                typeHandlers.Select(h => h.Class).Concat(serviceHandlers.Select(h => h.Service))
                    .Select(PreservationFile.SplitTypeName)
                    .Where(s => s.Assembly is null)
                    .Select(s => s.Type),
                StringComparer.Ordinal);
            Dictionary<string, string> assemblyByType = PreservationFile.ResolveAssemblies(wanted, ReferencePaths());

            int count = 0;
            count += EmitTypeHandlers(typeHandlers, assemblyByType);
            count += EmitServiceHandlers(serviceHandlers, assemblyByType);
            return count;
        }

        // An explicit assembly qualifier on the directive, else the build-output scan.
        private string? ResolveAssembly((string Type, string? Assembly) split, IReadOnlyDictionary<string, string> assemblyByType) =>
            split.Assembly ?? (assemblyByType.TryGetValue(split.Type, out string? a) ? a : null);

        // .asmx / .ashx → resultType 2 bound to the assembly that defines the class.
        private int EmitTypeHandlers(
            IReadOnlyList<(string VirtualPath, string Class)> handlers, IReadOnlyDictionary<string, string> assemblyByType)
        {
            int count = 0;
            foreach ((string vpath, string @class) in handlers)
            {
                (string Type, string? Assembly) split = PreservationFile.SplitTypeName(@class);
                string? assembly = ResolveAssembly(split, assemblyByType);
                if (assembly is null)
                {
                    Log.LogError(null, "TWF004", null, null, 0, 0, 0, 0,
                        "WebForms handler '{0}' class '{1}' was not found in any build output assembly, so it cannot be precompiled. Ensure the assembly defining it is referenced (and deployed to bin), or exclude the file via WebFormsGenHandlerExclude.", vpath, split.Type);
                    continue;
                }

                File.WriteAllText(Path.Combine(OutputDir, PreservationFile.Name(vpath)),
                    PreservationFile.PreserveType(PreservationFile.CompiledType, vpath, assembly, split.Type));
                count++;
            }

            return count;
        }

        // .svc → resultType 5 custom string: app-relative path | factory | service
        // type | the assembly defining it (the one entry the WCF host reads, to
        // resolve the simple-named service type).
        private int EmitServiceHandlers(
            IReadOnlyList<(string VirtualPath, string Service, string Factory)> handlers, IReadOnlyDictionary<string, string> assemblyByType)
        {
            int count = 0;
            foreach ((string vpath, string service, string factory) in handlers)
            {
                (string Type, string? Assembly) split = PreservationFile.SplitTypeName(service);
                string? assembly = ResolveAssembly(split, assemblyByType);
                if (assembly is null)
                {
                    Log.LogError(null, "TWF004", null, null, 0, 0, 0, 0,
                        "WebForms .svc handler '{0}' service type '{1}' was not found in any build output assembly, so it cannot be precompiled. Ensure the assembly defining it is referenced (and deployed to bin), or exclude the file via WebFormsGenHandlerExclude.", vpath, split.Type);
                    continue;
                }

                File.WriteAllText(Path.Combine(OutputDir, PreservationFile.Name(vpath)),
                    PreservationFile.PreserveCustomString(vpath, PreservationFile.ServiceCustomString(vpath, factory, split.Type, assembly)));
                count++;
            }

            return count;
        }

        private IEnumerable<string> ReferencePaths() => ReferenceAssemblies.Select(a => a.GetMetadata("FullPath"));

        private static string ToVirtualPath(ITaskItem item, string root)
        {
            string full = item.GetMetadata("FullPath").Replace('\\', '/');
            string rel = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length)
                : Path.GetFileName(full);
            return "/" + rel.TrimStart('/');
        }

        private static string Mangle(string vpath)
        {
            var sb = new StringBuilder();
            foreach (char c in vpath.TrimStart('/'))
            {
                sb.Append(c == '/' || c == '\\' || c == '.' ? '_' : char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
