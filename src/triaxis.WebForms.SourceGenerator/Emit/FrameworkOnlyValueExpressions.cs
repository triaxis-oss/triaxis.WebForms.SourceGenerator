using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace triaxis.WebForms.SourceGenerator.Emit
{
    /// <summary>
    /// Per-property-type codegen for the markup-attribute values whose
    /// <see cref="System.ComponentModel.TypeConverter"/> the analyzer cannot
    /// load. The runtime converter for these types lives in
    /// <c>System.Web.dll</c> (and a Framework <c>System.Drawing.dll</c>),
    /// which is .NET-Framework-only — a modern Roslyn host running on
    /// .NET 6+ (and especially on Linux) cannot instantiate
    /// <c>UnitConverter</c> / <c>FontUnitConverter</c> / the Framework view
    /// of <c>WebColorConverter</c>, so we cannot use the Layer 1
    /// <c>TypeDescriptor.GetConverter(t).ConvertTo(InstanceDescriptor)</c>
    /// pathway. Each case in <see cref="TryEmit"/> hardcodes the
    /// construction shape <c>aspnet_compiler</c> emits, mirrored from the
    /// decompiled oracle.
    /// </summary>
    /// <remarks>
    /// To extend coverage to a new System.Web type:
    /// 1. Capture what <c>aspnet_compiler</c> emits for a real markup
    ///    binding against the property (decompile, or read the converter
    ///    source).
    /// 2. Add a case to <see cref="TryEmit"/> returning the construction
    ///    expression. Use
    ///    <see cref="MarkupSourceGenerator.StringLiteral(string)"/> for
    ///    any string-literal argument.
    /// 3. If the converter has structured parseable input (e.g. CSS units),
    ///    factor the parsing into a helper alongside
    ///    <see cref="UnitExpression"/> / <see cref="ColorExpression"/>.
    /// </remarks>
    internal static class FrameworkOnlyValueExpressions
    {
        // CSS unit literal — number followed by optional unit suffix.
        // Static so the pattern compiles once per process, not per call.
        private static readonly Regex s_unitLiteral = new Regex(
            @"^(-?\d+(?:\.\d+)?)\s*(px|pt|pc|in|mm|cm|em|ex|%)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string? TryEmit(ITypeSymbol propertyType, string value, Compilation compilation)
        {
            switch (propertyType.ToDisplayString())
            {
                case "System.Web.UI.WebControls.Unit":
                    return UnitExpression(value)
                        ?? $"global::System.Web.UI.WebControls.Unit.Parse({MarkupSourceGenerator.StringLiteral(value)})";

                case "System.Web.UI.WebControls.FontUnit":
                    return UnitExpression(value) is { } fu
                        ? $"new global::System.Web.UI.WebControls.FontUnit({fu})"
                        : $"global::System.Web.UI.WebControls.FontUnit.Parse({MarkupSourceGenerator.StringLiteral(value)})";

                case "System.Drawing.Color":
                    // Belt-and-suspenders: Layer 1 should handle Color via the
                    // real ColorConverter when its assembly is loadable. This
                    // branch covers hosts where it isn't (e.g. some Linux
                    // analyzer-host configurations lacking System.Drawing.Common).
                    return ColorExpression(propertyType, value);

                case "System.Type":
                    return TypeofExpression(value, compilation);

                default:
                    return null;
            }
        }

        // Mirrors Unit's parsing into the new Unit(value, UnitType) form the
        // ASP.NET compiler emits (rather than a runtime Unit.Parse). Returns
        // null for values that aren't a plain number + unit suffix (e.g.
        // "Auto"), letting the caller fall back to Unit.Parse.
        private static string? UnitExpression(string value)
        {
            Match m = s_unitLiteral.Match(value.Trim());
            if (!m.Success)
            {
                return null;
            }

            string number = m.Groups[1].Value;
            if (!number.Contains("."))
            {
                number += ".0";
            }

            string? unitType = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "" or "px" => "Pixel",
                "pt" => "Point",
                "pc" => "Pica",
                "in" => "Inch",
                "mm" => "Mm",
                "cm" => "Cm",
                "em" => "Em",
                "ex" => "Ex",
                "%" => "Percentage",
                _ => null,
            };

            return unitType is null
                ? null
                : $"new global::System.Web.UI.WebControls.Unit({number}, global::System.Web.UI.WebControls.UnitType.{unitType})";
        }

        // Mirrors ASP.NET's WebColorConverter: an HTML hex (#rgb / #rrggbb)
        // becomes Color.FromArgb(r, g, b); a named color becomes the
        // canonical static Color.<Name> property (resolved case-insensitively
        // against the Color type).
        private static string? ColorExpression(ITypeSymbol colorType, string value)
        {
            string v = value.Trim();
            if (v.Length == 0)
            {
                // An empty color attribute (ForeColor="") is Color.Empty, the
                // property's default — matches WebColorConverter on "".
                return "global::System.Drawing.Color.Empty";
            }

            if (v.StartsWith("#", StringComparison.Ordinal))
            {
                return TryParseHtmlColor(v, out int r, out int g, out int b)
                    ? $"global::System.Drawing.Color.FromArgb({r}, {g}, {b})"
                    : null;
            }

            foreach (ISymbol member in colorType.GetMembers())
            {
                if (member is IPropertySymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public } p
                    && string.Equals(p.Name, v, StringComparison.OrdinalIgnoreCase))
                {
                    return "global::System.Drawing.Color." + p.Name;
                }
            }

            return null;
        }

        private static bool TryParseHtmlColor(string hex, out int r, out int g, out int b)
        {
            r = g = b = 0;
            string h = hex.Substring(1);
            if (h.Length == 3)
            {
                h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
            }

            if (h.Length != 6
                || !int.TryParse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                || !int.TryParse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                || !int.TryParse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                return false;
            }

            return true;
        }

        // A Type-valued attribute (RadGrid column DataType="System.String")
        // becomes typeof(...), the way the ASP.NET compiler's TypeConverter
        // resolves it — but only when the named type actually resolves in
        // the consumer's compilation, so we never emit an uncompilable
        // typeof.
        private static string? TypeofExpression(string value, Compilation compilation)
        {
            string typeName = value.Trim();
            int comma = typeName.IndexOf(',');
            if (comma >= 0)
            {
                typeName = typeName.Substring(0, comma).Trim();
            }

            return compilation.GetTypeByMetadataName(typeName) != null
                ? $"typeof(global::{typeName})"
                : null;
        }
    }
}
