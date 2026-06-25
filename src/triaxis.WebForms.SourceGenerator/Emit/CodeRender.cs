using System;
using System.Collections.Generic;
using System.Text;

namespace triaxis.WebForms.SourceGenerator.Emit
{
    /// <summary>
    /// The single ERB tokenizer for inline <c>&lt;% … %&gt;</c> code blocks. Both
    /// the body render path (mixed literal/control/code regions) and attribute
    /// values share these grammar rules, so they share this splitter.
    /// </summary>
    internal static class CodeRender
    {
        /// Splits markup into segments: ('L', text) literal, ('=', expr)
        /// <c>&lt;%= %&gt;</c>, (':', expr) <c>&lt;%: %&gt;</c>, ('#', expr)
        /// <c>&lt;%# %&gt;</c> data-binding, (' ', code) <c>&lt;% %&gt;</c>
        /// statement. <c>&lt;%-- --%&gt;</c> comments are dropped.
        public static IEnumerable<(char Kind, string Text)> Split(string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                int open = text.IndexOf("<%", i, StringComparison.Ordinal);
                if (open < 0)
                {
                    yield return ('L', text.Substring(i));
                    yield break;
                }

                if (open > i)
                {
                    yield return ('L', text.Substring(i, open - i));
                }

                char kind = open + 2 < text.Length ? text[open + 2] : '\0';
                if (kind == '-' && open + 3 < text.Length && text[open + 3] == '-')
                {
                    int endComment = text.IndexOf("--%>", open + 4, StringComparison.Ordinal);
                    i = endComment < 0 ? text.Length : endComment + 4;
                    continue;
                }

                int contentStart = kind is '=' or ':' or '#' ? open + 3 : open + 2;
                int close = text.IndexOf("%>", contentStart, StringComparison.Ordinal);
                if (close < 0)
                {
                    // Unterminated — treat the rest as literal.
                    yield return ('L', text.Substring(open));
                    yield break;
                }

                string code = text.Substring(contentStart, close - contentStart).Trim();
                char segKind = kind switch
                {
                    '=' => '=',
                    ':' => ':',
                    '#' => '#',
                    _ => ' ',
                };
                yield return (segKind, code);
                i = close + 2;
            }
        }

        /// True when the value carries an inline <c>&lt;%= %&gt;</c> or
        /// <c>&lt;%: %&gt;</c> render expression — the forms that, embedded in a
        /// string attribute value, must be evaluated and concatenated rather than
        /// emitted verbatim. A <c>&lt;%# %&gt;</c> data-binding does not count
        /// (it wires the control's DataBinding event instead).
        public static bool HasRenderExpression(string value)
        {
            foreach ((char kind, string _) in Split(value))
            {
                if (kind is '=' or ':')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds a C# string expression for an attribute value carrying embedded
        /// <c>&lt;%= %&gt;</c> / <c>&lt;%: %&gt;</c> render blocks, mirroring the
        /// body render path: literal runs become string literals, <c>&lt;%= %&gt;</c>
        /// evaluates inline, <c>&lt;%: %&gt;</c> HTML-encodes. Returns null when the
        /// value mixes in a statement or data-binding block, which has no
        /// concatenation meaning here.
        /// </summary>
        public static string? TryBuildConcatenation(string value)
        {
            var parts = new List<string>();
            bool hasLiteral = false;
            foreach ((char kind, string text) in Split(value))
            {
                switch (kind)
                {
                    case 'L':
                        if (text.Length == 0)
                        {
                            continue;
                        }

                        hasLiteral = true;
                        parts.Add(CodeLiteral.Escape(text));
                        break;
                    case '=':
                        parts.Add($"({text})");
                        break;
                    case ':':
                        parts.Add($"global::System.Web.HttpUtility.HtmlEncode(global::System.Convert.ToString({text}))");
                        break;
                    default:
                        // A statement (<% %>) or data-binding (<%# %>) block can't
                        // participate in a value concatenation.
                        return null;
                }
            }

            // A lone <%= %> yields an object expression; prefix an empty literal so
            // the result is always string-typed for the target property.
            if (!hasLiteral)
            {
                parts.Insert(0, "\"\"");
            }

            return string.Join(" + ", parts);
        }
    }
}
