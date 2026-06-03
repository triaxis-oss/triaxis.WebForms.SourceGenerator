using System;
using System.CodeDom.Compiler;
using System.IO;

namespace triaxis.WebForms.SourceGenerator.Emit
{
    /// <summary>
    /// Lets generated C# flow as nested blocks over an <see cref="IndentedTextWriter"/>:
    /// <code>
    /// using (w.Block($"public {type} {method}()"))
    /// {
    ///     w.Line("return __ctrl;");
    /// }
    /// </code>
    /// <see cref="Block(IndentedTextWriter,string)"/> writes the header, opens a
    /// brace and indents the body; disposing the returned scope closes the brace.
    /// </summary>
    internal static class CodeWriter
    {
        // The compiler runs the generator on a host that does not ship
        // System.CodeDom in its shared framework, but it is reachable from the SDK,
        // so the type loads at analyzer runtime (verified against the corpus build).
        public static IndentedTextWriter Create(int indent = 0)
        {
            // Pin LF on BOTH the inner StringWriter and the IndentedTextWriter
            // itself — the latter's NewLine defaults to Environment.NewLine,
            // which would flip generator output to CRLF on Windows hosts and
            // blow Roslyn's source-text cache hash relative to Linux runs.
            var writer = new IndentedTextWriter(new StringWriter { NewLine = "\n" }, "    ");
            writer.NewLine = "\n";
            writer.Indent = indent;
            return writer;
        }

        public static string ToSource(this IndentedTextWriter writer)
        {
            writer.Flush();
            return writer.InnerWriter.ToString();
        }

        public static void Line(this IndentedTextWriter writer, string text)
        {
            writer.WriteLine(text);
        }

        /// <summary>A blank separator line with no indentation (matching the old
        /// <c>StringBuilder.AppendLine()</c>), so member spacing is unchanged.</summary>
        public static void Blank(this IndentedTextWriter writer)
        {
            writer.WriteLineNoTabs(string.Empty);
        }

        /// <summary>Splices an already-indented fragment (a control's build method,
        /// the tree body) in verbatim, leaving the writer's own indentation alone.
        /// Two preconditions, both held by the emitters: call only at the start of a
        /// line (so no half-written indent is pending), and the fragment must already
        /// carry the column it should land at — it was built by another
        /// <see cref="Create"/> writer at the matching indent.</summary>
        public static void Raw(this IndentedTextWriter writer, string fragment)
        {
            writer.InnerWriter.Write(fragment);
        }

        public static BlockScope Block(this IndentedTextWriter writer)
        {
            return new BlockScope(writer, null);
        }

        public static BlockScope Block(this IndentedTextWriter writer, string header)
        {
            return new BlockScope(writer, header);
        }

        internal readonly struct BlockScope : IDisposable
        {
            private readonly IndentedTextWriter _writer;

            public BlockScope(IndentedTextWriter writer, string? header)
            {
                if (header != null)
                {
                    writer.WriteLine(header);
                }

                writer.WriteLine("{");
                writer.Indent++;
                _writer = writer;
            }

            public void Dispose()
            {
                _writer.Indent--;
                _writer.WriteLine("}");
            }
        }
    }
}
