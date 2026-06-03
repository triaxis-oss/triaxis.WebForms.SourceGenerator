using System.Globalization;
using System.Text;

namespace triaxis.WebForms.SourceGenerator.Emit
{
    /// <summary>
    /// Escapes an arbitrary string into a C# double-quoted string literal,
    /// including the surrounding quotes. The single source of truth for
    /// every place the generator emits a string from markup; previously
    /// duplicated as <c>Literal</c> / <c>Quote</c> / <c>EscapeLiteral</c>
    /// in several files with inconsistent escape sets (some missed
    /// <c>\r\n\t</c>, producing invalid C# when an attribute value
    /// contained a raw newline).
    /// </summary>
    internal static class CodeLiteral
    {
        public static string Escape(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\0': sb.Append("\\0"); break;
                    case '\a': sb.Append("\\a"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\v': sb.Append("\\v"); break;
                    default:
                        // C0 control chars not named above (< 0x20) escape to
                        // \uXXXX; anything else — including non-ASCII — flows
                        // through unchanged. The generated file is UTF-8 and
                        // the C# compiler handles arbitrary code points fine.
                        if (c < 0x20)
                        {
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c);
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }
    }
}
