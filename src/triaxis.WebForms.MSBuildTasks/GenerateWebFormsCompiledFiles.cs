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
        // App_Browsers.compiled sidecar pointing BuildManager at the
        // source-generated ASP.ApplicationBrowserCapabilitiesFactory so the
        // runtime resolves it at app start; without this file the factory
        // class is present but never wired and .browser entries silently
        // don't apply.
        public ITaskItem[] BrowserFiles { get; set; } = Array.Empty<ITaskItem>();
        // .asmx / .ashx / .svc handlers. Unlike markup these aren't source-
        // generated into page types — each directive names a prebuilt type a
        // non-updatable precompiled app still needs a .compiled sidecar to reach.
        public ITaskItem[] HandlerFiles { get; set; } = Array.Empty<ITaskItem>();
        // Build outputs scanned to bind a handler to the assembly that defines
        // its type (the host assembly for in-project code-behind, a referenced
        // assembly otherwise) and to list the deployed assembly closure a .svc
        // carries for service-type resolution.
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

                string resultType = isGlobalAsax ? "8" : "3";
                string typeName = isGlobalAsax ? "ASP._global_asax" : "ASP." + Mangle(vpath);
                string fileName = isGlobalAsax ? "App_global.asax.compiled" : PreservationFile.Name(vpath);
                File.WriteAllText(Path.Combine(OutputDir, fileName),
                    PreservationFile.PreserveType(resultType, vpath, AssemblyName, typeName));
                count++;
            }

            count += EmitHandlerCompiled(root);

            if (BrowserFiles.Length > 0)
            {
                // resultType="9" = BuildResultType.AppBrowserCapabilitiesCompiler.
                // BuildManager looks the sidecar up by the fixed name
                // App_Browsers.compiled at app start.
                File.WriteAllText(Path.Combine(OutputDir, "App_Browsers.compiled"),
                    PreservationFile.PreserveType("9", "/App_Browsers", AssemblyName, "ASP.ApplicationBrowserCapabilitiesFactory"));
                count++;
            }

            Log.LogMessage(MessageImportance.High, "Generated " + count + " .compiled files in " + OutputDir);
            // A handler that couldn't be bound logged an error (TWF004): fail the
            // build rather than ship a deployment that 500s at first request.
            return !Log.HasLoggedErrors;
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

            int count = 0;
            count += EmitTypeHandlers(typeHandlers);
            count += EmitServiceHandlers(serviceHandlers);
            return count;
        }

        // .asmx / .ashx → resultType 2 bound to the assembly that defines the class.
        private int EmitTypeHandlers(IReadOnlyList<(string VirtualPath, string Class)> handlers)
        {
            if (handlers.Count == 0)
            {
                return 0;
            }

            var parsed = handlers
                .Select(h => (h.VirtualPath, Type: PreservationFile.SplitTypeName(h.Class)))
                .ToList();

            // Only bare "Ns.Type" names need a scan; an assembly-qualified directive
            // already states where its type lives.
            var wanted = new HashSet<string>(
                parsed.Where(p => p.Type.Assembly is null).Select(p => p.Type.Type), StringComparer.Ordinal);
            Dictionary<string, string> assemblyByType = PreservationFile.ResolveAssemblies(wanted, ReferencePaths());

            int count = 0;
            foreach ((string vpath, (string type, string? explicitAssembly)) in parsed)
            {
                string? assembly = explicitAssembly
                    ?? (assemblyByType.TryGetValue(type, out string? resolved) ? resolved : null);
                if (assembly is null)
                {
                    Log.LogError(null, "TWF004", null, null, 0, 0, 0, 0,
                        "WebForms handler '{0}' class '{1}' was not found in any build output assembly, so it cannot be precompiled. Ensure the assembly defining it is referenced (and deployed to bin), or exclude the file via WebFormsGenHandlerExclude.", vpath, type);
                    continue;
                }

                File.WriteAllText(Path.Combine(OutputDir, PreservationFile.Name(vpath)),
                    PreservationFile.PreserveType(PreservationFile.CompiledType, vpath, assembly, type));
                count++;
            }

            return count;
        }

        // .svc → resultType 5 custom string carrying the service type and the
        // deployed assembly closure the WCF host resolves it against.
        private int EmitServiceHandlers(IReadOnlyList<(string VirtualPath, string Service, string Factory)> handlers)
        {
            if (handlers.Count == 0)
            {
                return 0;
            }

            List<string> closure = PreservationFile.AssemblyFullNames(ReferencePaths());
            foreach ((string vpath, string service, string factory) in handlers)
            {
                // aspnet_compiler writes the bare type name; the host resolves it
                // against the closure, so drop any assembly qualifier on Service.
                string serviceType = PreservationFile.SplitTypeName(service).Type;
                string customString = PreservationFile.ServiceCustomString(vpath, factory, serviceType, closure);
                File.WriteAllText(Path.Combine(OutputDir, PreservationFile.Name(vpath)),
                    PreservationFile.PreserveCustomString(vpath, customString));
            }

            return handlers.Count;
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
