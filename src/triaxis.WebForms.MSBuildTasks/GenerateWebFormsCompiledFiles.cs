using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace triaxis.WebForms.MSBuildTasks
{
    /// <summary>
    /// Emits the lean <c>.compiled</c> sidecar files that point ASP.NET's
    /// <c>BuildManager</c> at the Roslyn-generated <c>ASP.&lt;name&gt;_aspx</c>
    /// types in the main app assembly, replacing the
    /// <c>aspnet_compiler</c> / <c>aspnet_merge</c> precompile.
    /// </summary>
    /// <remarks>
    /// The runtime (precompiled, non-updatable) reads only
    /// <c>resultType</c> / <c>assembly</c> / <c>type</c> — hashes and
    /// <c>filedeps</c> are ignored — so each file is a four-field template.
    /// The filename is the cache key <c>BuildManager</c> derives from the
    /// virtual path (<c>System.Web.Util.StringUtil.GetStringHashCode</c> of
    /// the app-relative dir), ported byte-for-byte below.
    /// </remarks>
    public sealed class TriaxisGenerateWebFormsCompiledFiles : Task
    {
        [Required] public string ProjectDir { get; set; } = string.Empty;
        [Required] public string OutputDir { get; set; } = string.Empty;
        [Required] public string AssemblyName { get; set; } = string.Empty;
        [Required] public ITaskItem[] MarkupFiles { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            Directory.CreateDirectory(OutputDir);
            string root = Path.GetFullPath(ProjectDir).Replace('\\', '/').TrimEnd('/') + "/";
            int count = 0;

            foreach (ITaskItem item in MarkupFiles)
            {
                string full = item.GetMetadata("FullPath").Replace('\\', '/');
                string rel = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? full.Substring(root.Length)
                    : Path.GetFileName(full);
                string vpath = "/" + rel.TrimStart('/');

                bool isGlobalAsax = string.Equals(Path.GetFileName(vpath), "global.asax", StringComparison.OrdinalIgnoreCase);
                if (isGlobalAsax)
                {
                    vpath = "/global.asax";
                }

                string resultType = isGlobalAsax ? "8" : "3";
                string typeName = isGlobalAsax ? "ASP._global_asax" : "ASP." + Mangle(vpath);
                string content =
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                    "<preserve resultType=\"" + resultType + "\" virtualPath=\"" + vpath + "\" assembly=\"" +
                    AssemblyName + "\" type=\"" + typeName + "\" />\r\n";

                // global.asax is looked up by BuildManager.GlobalAsaxAssemblyName
                // ("App_global.asax"), not the page cache-key hash.
                string fileName = isGlobalAsax ? "App_global.asax.compiled" : PreservationFileName(vpath);
                File.WriteAllText(Path.Combine(OutputDir, fileName), content);
                count++;
            }

            Log.LogMessage(MessageImportance.High, "Generated " + count + " .compiled files in " + OutputDir);
            return true;
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

        private static string PreservationFileName(string vpath)
        {
            string appRel = ("~" + (vpath.StartsWith("/") ? vpath : "/" + vpath)).ToLowerInvariant().TrimEnd('/');
            int slash = appRel.LastIndexOf('/');
            string name = appRel.Substring(slash + 1);
            string dir = slash <= 0 ? "/" : appRel.Substring(0, slash);
            return name + "." + GetStringHashCode(dir).ToString("x", CultureInfo.InvariantCulture) + ".compiled";
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
