using System.Text;

namespace triaxis.WebForms.SourceGenerator.Emit
{
    internal static class GeneratedNaming
    {
        public const string Namespace = "ASP";

        /// <summary>
        /// The class name ASP.NET's compiler derives from a virtual path:
        /// lower-cased with every separator and the extension dot turned into
        /// `_`. `/Forms/frmHome.aspx` → `forms_frmhome_aspx`.
        /// </summary>
        public static string TypeNameFromVirtualPath(string virtualPath)
        {
            var sb = new StringBuilder(virtualPath.Length);
            foreach (char c in virtualPath.TrimStart('/'))
            {
                sb.Append(c is '/' or '\\' or '.' ? '_' : char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }

        public static string AppRelativeVirtualPath(string virtualPath)
        {
            return "~" + (virtualPath.StartsWith("/") ? virtualPath : "/" + virtualPath);
        }
    }
}
