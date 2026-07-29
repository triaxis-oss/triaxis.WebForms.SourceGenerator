using System.Text;
using Microsoft.CodeAnalysis.CSharp;

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

        /// <summary>
        /// <paramref name="name"/> as a C# identifier: every character an
        /// identifier can't hold (<c>My-Theme</c>) becomes <c>_</c>, and a
        /// leading <c>_</c> goes in front of a name that would otherwise start
        /// with a digit. Names differing only in those characters map to the
        /// same identifier and collide.
        /// </summary>
        /// <remarks>
        /// The result is a metadata name — <see cref="Escaped"/> adds the
        /// escape a declaration needs. It must stay in sync with
        /// <c>PreservationFile.Identifier</c> in the MSBuild tasks assembly,
        /// which derives the type name a theme's <c>.compiled</c> sidecar points
        /// at — a mismatch leaves the theme unresolvable at runtime. A test
        /// asserts the two agree.
        /// </remarks>
        public static string Identifier(string name)
        {
            var sb = new StringBuilder(name.Length + 1);
            foreach (char c in name)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }

            // An identifier can't start with a digit; a leading `_` fixes that
            // without mangling the rest of the name.
            if (sb.Length == 0 || char.IsDigit(sb[0]))
            {
                sb.Insert(0, '_');
            }

            return sb.ToString();
        }

        /// <summary>
        /// <paramref name="identifier"/> as it has to be written to declare it:
        /// a theme folder named <c>default</c> yields a valid but reserved
        /// identifier, which the declaration escapes and the metadata name (and
        /// so the <c>.compiled</c> sidecar) must not.
        /// </summary>
        public static string Escaped(string identifier)
        {
            return SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;
        }

        public static string AppRelativeVirtualPath(string virtualPath)
        {
            return "~" + (virtualPath.StartsWith("/") ? virtualPath : "/" + virtualPath);
        }
    }
}
