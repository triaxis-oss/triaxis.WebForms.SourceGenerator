using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using triaxis.WebForms.SourceGenerator.Emit;
using triaxis.WebForms.SourceGenerator.Model;
using triaxis.WebForms.SourceGenerator.Parsing;

namespace triaxis.WebForms.SourceGenerator;

/// <summary>
/// Discovers the <c>.aspx</c>/<c>.ascx</c>/<c>.master</c> corpus supplied as
/// <c>AdditionalFiles</c> and emits a virtual-path manifest plus the full
/// generated type per markup file (frame + control tree). Control types are
/// resolved against the compilation; unresolved tags fall back to verbatim
/// literals so the output always compiles.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MarkupSourceGenerator : IIncrementalGenerator
{
    // File extensions ASP.NET's BuildManager compiles. Add a new extension
    // here when extending coverage (e.g. ".svc" for WCF service files —
    // not supported today). MarkupTreeFolder + PageEmitter assume a page,
    // user control, or master shape, so a new extension also wants a
    // MarkupKind mapping in MarkupParserDriver.
    private static readonly ImmutableHashSet<string> s_markupExtensions =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, ".aspx", ".ascx", ".master", ".asax");

    // Hoisted regexes — Regex.Match(string, string, …) compiles the pattern
    // on every call; static readonly instances reuse the compiled state.
    private static readonly Regex s_inheritsAttribute =
        new Regex("Inherits\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex s_bindExpression =
        new Regex("^Bind\\(\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<MarkupFile> files = context.AdditionalTextsProvider
            .Where(static text => s_markupExtensions.Contains(Path.GetExtension(text.Path)))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellationToken) => ReadFile(pair.Left, pair.Right, cancellationToken));

        // One parse per web.config produces all four bindings the generator
        // observes (prefix map, page namespaces, page defaults, user
        // controls); the Collect+Merge then folds them across files.
        IncrementalValueProvider<WebConfigBindings> webConfig = context.AdditionalTextsProvider
            .Where(static text => string.Equals(Path.GetFileName(text.Path), "web.config", StringComparison.OrdinalIgnoreCase))
            .Select(static (text, ct) => WebConfigParser.Parse(text.GetText(ct)?.ToString() ?? string.Empty))
            .Collect()
            .Select(static (parsed, _) => WebConfigParser.Merge(parsed));

        // Generated ASP type → its codebehind class (the markup's Inherits), so a
        // user control reference can bind its custom attributes against the real
        // codebehind type rather than the UserControl base.
        IncrementalValueProvider<ImmutableDictionary<string, string>> codeBehind = files
            .Select(static (f, _) => (Type: GeneratedNaming.Namespace + "." + GeneratedNaming.TypeNameFromVirtualPath(f.VirtualPath), Inherits: ParseInherits(f.Content)))
            .Collect()
            .Select(static (arr, _) => MergeCodeBehind(arr));

        context.RegisterSourceOutput(files.Collect(), static (spc, all) => EmitManifest(spc, all));

        // Aggregate every App_Browsers\*.browser file into a single
        // ApplicationBrowserCapabilitiesFactory emission. Each .browser
        // contributes a flat list of entries; the emitter folds them into
        // the parent / refining tree.
        IncrementalValueProvider<ImmutableArray<Model.BrowserEntry>> browserEntries = context.AdditionalTextsProvider
            .Where(static text => string.Equals(Path.GetExtension(text.Path), ".browser", StringComparison.OrdinalIgnoreCase))
            .Select(static (text, ct) => Parsing.BrowserCapabilitiesParser.Parse(text.GetText(ct)?.ToString() ?? string.Empty, text.Path))
            .Collect()
            .Select(static (perFile, _) => perFile.SelectMany(e => e).ToImmutableArray());

        context.RegisterSourceOutput(browserEntries.Combine(context.CompilationProvider), static (spc, pair) =>
        {
            (ImmutableArray<Model.BrowserEntry> entries, Compilation compilation) = pair;
            if (entries.Length == 0) return;
            string source = Emit.BrowserCapabilitiesFactoryEmitter.Emit(entries, compilation);
            spc.AddSource("ApplicationBrowserCapabilitiesFactory.g.cs", SourceText.From(source, Encoding.UTF8));
        });

        // Combine returns nested (Left, Right) tuples; project them once into
        // a named PageInput so downstream code reads by name instead of
        // decoding .Left.Left.Right.Left chains.
        IncrementalValueProvider<PageContext> pageContext = webConfig.Combine(codeBehind)
            .Select(static (x, _) => new PageContext(x.Left, x.Right));

        IncrementalValuesProvider<PageInput> pageInputs =
            files.Combine(pageContext).Combine(context.CompilationProvider)
                .Select(static (x, _) => new PageInput(
                    File: x.Left.Left,
                    Context: x.Left.Right,
                    Compilation: x.Right));

        context.RegisterSourceOutput(pageInputs, static (spc, input) => EmitPage(spc, input));
    }

    // Per-page context the generator needs: web.config bindings + the
    // ASP-type → codebehind map (built from the markup corpus itself).
    private readonly record struct PageContext(
        WebConfigBindings WebConfig,
        ImmutableDictionary<string, string> CodeBehind);

    // Per-file emission input: the markup file plus the shared page-level
    // context plus the consumer compilation symbols are resolved against.
    private readonly record struct PageInput(MarkupFile File, PageContext Context, Compilation Compilation);

    private static MarkupFile ReadFile(AdditionalText text, AnalyzerConfigOptionsProvider options, CancellationToken cancellationToken)
    {
        string virtualPath = ToVirtualPath(text.Path, options);
        string content = text.GetText(cancellationToken)?.ToString() ?? string.Empty;
        return new MarkupFile(virtualPath, content);
    }

    /// <summary>
    /// Web-root-relative path with a leading slash and forward separators, e.g.
    /// <c>/Forms/frmHome.aspx</c> — the key System.Web's BuildManager uses to
    /// resolve a request to a compiled type, so the rest of the generator keys
    /// off the same value.
    /// </summary>
    private static string ToVirtualPath(string filePath, AnalyzerConfigOptionsProvider options)
    {
        string normalized = filePath.Replace('\\', '/');
        if (options.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out string? projectDir)
            && !string.IsNullOrEmpty(projectDir))
        {
            string root = projectDir.Replace('\\', '/').TrimEnd('/') + "/";
            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(root.Length);
            }
        }

        return "/" + normalized.TrimStart('/');
    }

    private static void EmitGlobalAsax(SourceProductionContext context, string content)
    {
        Match m = s_inheritsAttribute.Match(content);
        string baseType = m.Success ? "global::" + m.Groups[1].Value : "global::System.Web.HttpApplication";

        IndentedTextWriter w = CodeWriter.Create();
        w.Line("// <auto-generated/>");
        w.Line("#nullable disable");
        using (w.Block("namespace ASP"))
        using (w.Block($"public class _global_asax : {baseType}"))
        {
            w.Line("public _global_asax() { }");
        }

        context.AddSource("_global_asax.g.cs", SourceText.From(w.ToSource(), Encoding.UTF8));
    }

    private static void EmitManifest(SourceProductionContext context, ImmutableArray<MarkupFile> files)
    {
        IEnumerable<string> ordered = files
            .Select(static f => f.VirtualPath)
            .OrderBy(static p => p, StringComparer.Ordinal);

        IndentedTextWriter w = CodeWriter.Create();
        w.Line("// <auto-generated/>");
        w.Line("#nullable enable");
        using (w.Block("namespace ASP.Generated"))
        using (w.Block("internal static class MarkupManifest"))
        {
            w.Line($"internal const int Count = {files.Length};");
            w.Blank();
            w.Line("internal static readonly string[] VirtualPaths = new string[]");
            w.Line("{");
            w.Indent++;
            foreach (string path in ordered)
            {
                w.Line($"{StringLiteral(path)},");
            }

            w.Indent--;
            w.Line("};");
        }

        context.AddSource("MarkupManifest.g.cs", SourceText.From(w.ToSource(), Encoding.UTF8));
    }

    // Namespaces ASP.NET's compiler implicitly imports into every generated
    // page so inline code (<%= %> / <%# %>) can reference common BCL and
    // System.Web types without an @Import. Sourced from the framework's
    // own page-default imports — adding entries here mirrors what an
    // <%@ Import Namespace="…" %> directive would do for every page, so
    // prefer per-page imports unless a namespace is genuinely site-wide.
    private static readonly string[] s_defaultImports =
    {
        "System", "System.Collections", "System.Collections.Generic", "System.Collections.Specialized",
        "System.Configuration", "System.Text", "System.Text.RegularExpressions", "System.Linq",
        "System.Web", "System.Web.Caching", "System.Web.SessionState", "System.Web.Security",
        "System.Web.Profile", "System.Web.UI", "System.Web.UI.WebControls",
        "System.Web.UI.WebControls.WebParts", "System.Web.UI.HtmlControls", "System.Xml.Linq",
    };

    private static void EmitPage(SourceProductionContext context, PageInput input)
    {
        (MarkupFile file, PageContext ctx, Compilation compilation) = input;
        (WebConfigBindings webConfig, ImmutableDictionary<string, string> codeBehindMap) = ctx;
        (ImmutableDictionary<string, string> prefixes,
            ImmutableArray<string> pageNamespaces,
            ImmutableDictionary<string, string> pageDefaults,
            ImmutableDictionary<string, string> userControls) = webConfig;

        // global.asax compiles to ASP._global_asax : <codebehind HttpApplication>.
        // Without it a non-updatable precompiled app never runs Application_Start.
        if (string.Equals(Path.GetFileName(file.VirtualPath), "global.asax", StringComparison.OrdinalIgnoreCase))
        {
            EmitGlobalAsax(context, file.Content);
            return;
        }

        ParsedMarkup parsed = MarkupParserDriver.Parse(file.VirtualPath, new StringReader(file.Content));
        if (parsed.Directive is null)
        {
            return;
        }

        // Imports the markup's inline code (<%= %> / <% %>) relies on: ASP.NET
        // defaults + web.config <pages><namespaces> + per-page <%@ Import %>.
        // Validated against the compilation so we never emit a using for a
        // namespace that isn't referenced.
        var usings = s_defaultImports
            .Concat(pageNamespaces)
            .Concat(parsed.Imports)
            .Distinct(StringComparer.Ordinal)
            .Where(ns => NamespaceExists(compilation, ns))
            .ToList();

        // Fold the page's own <%@ Register %> directives over the site-wide
        // web.config registrations (namespace prefixes and user controls).
        var mergedPrefixes = new Dictionary<string, string>(prefixes, StringComparer.OrdinalIgnoreCase);
        var mergedUserControls = new Dictionary<string, string>(userControls, StringComparer.OrdinalIgnoreCase);
        string pageDirectory = file.VirtualPath.Substring(0, file.VirtualPath.LastIndexOf('/') + 1);
        foreach (TagRegistration registration in parsed.Registrations)
        {
            if (!string.IsNullOrWhiteSpace(registration.TagName) && !string.IsNullOrWhiteSpace(registration.Src))
            {
                mergedUserControls[registration.Prefix + ":" + registration.TagName] = WebConfigParser.UserControlMetadata(registration.Src!, pageDirectory);
            }
            else if (!string.IsNullOrWhiteSpace(registration.Namespace))
            {
                mergedPrefixes[registration.Prefix] = registration.Namespace!;
            }
        }

        var resolver = new ControlTypeResolver(mergedPrefixes, metadataName => CanonicalConstructibleType(compilation, metadataName), mergedUserControls);

        var serverPrefixes = new HashSet<string>(mergedPrefixes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string key in mergedUserControls.Keys)
        {
            serverPrefixes.Add(key.Substring(0, key.IndexOf(':')));
        }

        ServerControlNode root = MarkupTreeFolder.Fold(file.VirtualPath, file.Content, serverPrefixes, out _,
            (parentMeta, rawName, isServer) => ResolveChild(compilation, resolver, parentMeta, rawName, isServer));

        INamedTypeSymbol? codeBehind = string.IsNullOrWhiteSpace(parsed.Directive.Inherits)
            ? null
            : compilation.GetTypeByMetadataName(parsed.Directive.Inherits!);

        // Collects property types resolved via the Layer-3 Parse/ctor fallback so
        // we can fire TWF002 once per unique type after the page emits.
        var layer3Fallbacks = new HashSet<string>(StringComparer.Ordinal);

        // Cache the System.Web.UI.Control anchor once per emit — both
        // IsControlType and IsThemeable read it for every control on the
        // page (the per-page hit count scales with control density).
        INamedTypeSymbol? controlSymbol = compilation.GetTypeByMetadataName("System.Web.UI.Control");

        AttributeBinder binder = (metadata, name, value, container) => BindAttribute(compilation, codeBehind, metadata, name, value, container, layer3Fallbacks);
        ChildClassifier classify = (parentMeta, childLocal, childIsServer) => ClassifyChild(compilation, parentMeta, childLocal, childIsServer);
        ControlContainerPredicate isControlContainer = parentMeta => IsControlContainer(compilation, parentMeta);
        System.Func<string, bool> isThemeable = metadata => IsThemeable(compilation, controlSymbol, metadata);
        System.Func<string, bool> isControl = metadata => IsControlType(compilation, controlSymbol, metadata);
        // For a user control (ASP type not in this compilation) resolve its
        // codebehind class so its custom properties bind, falling back to the
        // UserControl base when unknown.
        System.Func<string, string> userControlBindingType = aspType =>
            codeBehindMap.TryGetValue(aspType, out string? cb) && compilation.GetTypeByMetadataName(cb) != null
                ? cb
                : "System.Web.UI.UserControl";

        string source = PageEmitter.Emit(parsed.Directive, file.VirtualPath, root, resolver, file.Content, binder,
            classify, isControlContainer, isThemeable, isControl, userControlBindingType, pageDefaults, out _,
            out IReadOnlyList<Emit.ControlTreeEmitter.FieldBindingMismatch> fieldBindingMismatches,
            (id, controlMetadata) => ResolveCodeBehindField(compilation, codeBehind, id, controlMetadata),
            usings,
            id => CodeBehindHasMember(codeBehind, id));

        foreach (string typeName in layer3Fallbacks)
        {
            context.ReportDiagnostic(Diagnostic.Create(s_layer3Fallback, Location.None, typeName));
        }

        foreach (Emit.ControlTreeEmitter.FieldBindingMismatch mismatch in fieldBindingMismatches)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                s_fieldBindingMismatch, Location.None,
                mismatch.ControlId, mismatch.ResolvedType, mismatch.ExistingFieldType, file.VirtualPath));
        }

        string hint = GeneratedNaming.TypeNameFromVirtualPath(file.VirtualPath) + ".g.cs";
        context.AddSource(hint, SourceText.From(source, Encoding.UTF8));
    }

    // Binds a markup attribute to the resolved control type: a typed property
    // assignment when a matching settable property exists and its type is one we
    // can emit a literal for; otherwise IAttributeAccessor.SetAttribute when the
    // type supports it (HtmlControls/WebControls); otherwise skipped. This is
    // what keeps us off the InvalidCast cliff for Control-derived types like
    // RadScriptManager that don't implement IAttributeAccessor.
    private static AttributeBinding BindAttribute(Compilation compilation, INamedTypeSymbol? codeBehind, string controlTypeMetadataName, string name, string value, string? containerType, ICollection<string>? layer3Fallbacks)
    {
        if (compilation.GetTypeByMetadataName(controlTypeMetadataName) is not INamedTypeSymbol type)
        {
            return AttributeBinding.Skip("control type not found");
        }

        // <%# … %> data-binding expression.
        string trimmed = value.Trim();
        if (trimmed.StartsWith("<%#", StringComparison.Ordinal) && trimmed.EndsWith("%>", StringComparison.Ordinal))
        {
            IPropertySymbol? bound = FindSettableProperty(type, name);
            string inner = trimmed.Substring(3, trimmed.Length - 5).Trim();

            // Two-way Bind("Field") reads back into the template's ExtractValues
            // method; for rendering it behaves like one-way Eval.
            string? bindField = ReadBindField(inner);
            string expr = inner.Replace("Bind(", "Eval(");

            // `Container`-relative expressions only compile when we know the
            // data-item type (the enclosing template's [TemplateContainer]).
            bool containerOk = !expr.Contains("Container") || containerType != null;
            if (FindEvent(type, "DataBinding") != null && containerOk)
            {
                if (bound != null)
                {
                    return AttributeBinding.DataBinding(bound.Name, DataBindConversion(bound.Type, expr), bindField);
                }

                // A data-bound markup attribute with no matching property
                // (<label for="<%# … %>">) is written through SetAttribute, the way
                // the ASP.NET compiler binds it.
                if (ImplementsAttributeAccessor(type))
                {
                    return AttributeBinding.AttributeDataBinding(name, $"global::System.Convert.ToString({expr}, global::System.Globalization.CultureInfo.CurrentCulture)");
                }
            }

            return AttributeBinding.Skip($"data-binding '{name}' not emitted (Container-relative without a known container, or non-bindable)");
        }

        IPropertySymbol? property = FindSettableProperty(type, name);
        if (property != null)
        {
            string? expression = BuildValueExpression(property.Type, value, compilation, layer3Fallbacks);
            if (expression != null)
            {
                return AttributeBinding.Property(property.Name, expression);
            }

            return ImplementsAttributeAccessor(type)
                ? AttributeBinding.Attribute()
                : AttributeBinding.Skip($"unsupported property type {property.Type.ToDisplayString()}");
        }

        // Persisted subproperty: "Font-Bold" → Font.Bold, "HeaderStyle-Width" →
        // HeaderStyle.Width. The hyphen walks a path of complex-type properties.
        if (TryBindSubProperty(type, name, value, compilation, layer3Fallbacks) is { } subProperty)
        {
            return subProperty;
        }

        // "OnClick" → the Click event, wired to a codebehind handler method.
        if (name.Length > 2 && name.StartsWith("On", StringComparison.OrdinalIgnoreCase) && codeBehind != null)
        {
            IEventSymbol? evt = FindEvent(type, name.Substring(2));
            if (evt != null && HasMethod(codeBehind, value))
            {
                return AttributeBinding.Event(evt.Name, value);
            }
        }

        return ImplementsAttributeAccessor(type)
            ? AttributeBinding.Attribute()
            : AttributeBinding.Skip("no matching property/event and type is not IAttributeAccessor");
    }

    // The data field of a two-way Bind("Field") / Bind('Field') expression.
    private static string? ReadBindField(string expression)
    {
        Match match = s_bindExpression.Match(expression);
        return match.Success ? match.Groups[1].Value : null;
    }

    // Wraps a data-binding expression in the conversion to the property's type,
    // mirroring aspnet_compiler (Convert.ToX(...) / cast). `Eval` resolves to the
    // inherited TemplateControl.Eval against the current data item.
    private static string DataBindConversion(ITypeSymbol propertyType, string expr)
    {
        // Nullable<T>: convert to T; the result assigns implicitly to T?.
        if (propertyType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            return DataBindConversion(nullable.TypeArguments[0], expr);
        }

        return propertyType.SpecialType switch
        {
            SpecialType.System_String => $"global::System.Convert.ToString({expr}, global::System.Globalization.CultureInfo.CurrentCulture)",
            SpecialType.System_Boolean => $"global::System.Convert.ToBoolean({expr})",
            SpecialType.System_Int32 => $"global::System.Convert.ToInt32({expr})",
            SpecialType.System_Int64 => $"global::System.Convert.ToInt64({expr})",
            SpecialType.System_Double => $"global::System.Convert.ToDouble({expr})",
            SpecialType.System_Decimal => $"global::System.Convert.ToDecimal({expr})",
            SpecialType.System_DateTime => $"global::System.Convert.ToDateTime({expr})",
            // Object / enums / reference types: cast the expression to the type.
            _ => $"({propertyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})({expr})",
        };
    }

    // Resolves a hyphen-delimited subproperty path ("Font-Bold", "ItemStyle-Font-Size")
    // to a "Font.Bold" assignment against the nested complex-type property,
    // converting the value to the leaf property's type.
    private static AttributeBinding? TryBindSubProperty(INamedTypeSymbol type, string name, string value, Compilation compilation, ICollection<string>? layer3Fallbacks)
    {
        int dash = name.IndexOf('-');
        if (dash <= 0)
        {
            return null;
        }

        if (FindGettableProperty(type, name.Substring(0, dash)) is not { Type: INamedTypeSymbol outerType } outer)
        {
            return null;
        }

        string rest = name.Substring(dash + 1);
        if (rest.IndexOf('-') > 0)
        {
            return TryBindSubProperty(outerType, rest, value, compilation, layer3Fallbacks) is { } nested
                ? AttributeBinding.Property(outer.Name + "." + nested.PropertyName, nested.ValueExpression!)
                : null;
        }

        if (FindSettableProperty(outerType, rest) is not { } inner
            || BuildValueExpression(inner.Type, value, compilation, layer3Fallbacks) is not { } expression)
        {
            return null;
        }

        return AttributeBinding.Property(outer.Name + "." + inner.Name, expression);
    }

    private static bool CodeBehindHasMember(INamedTypeSymbol? codeBehind, string id)
    {
        for (INamedTypeSymbol? t = codeBehind; t != null; t = t.BaseType)
        {
            if (t.GetMembers(id).Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static IEventSymbol? FindEvent(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? t = type; t != null; t = t.BaseType)
        {
            foreach (ISymbol member in t.GetMembers())
            {
                if (member is IEventSymbol e
                    && e.DeclaredAccessibility == Accessibility.Public
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }
        }

        return null;
    }

    private static bool HasMethod(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? t = type; t != null; t = t.BaseType)
        {
            foreach (ISymbol member in t.GetMembers())
            {
                if (member is IMethodSymbol m
                    && m.MethodKind == MethodKind.Ordinary
                    && string.Equals(m.Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Returns the canonical codebehind member name to assign the control to
    // (this.<name> = __ctrl;), or null when there's no settable member of a
    // compatible type — keeping the assignment compile-safe.
    // Tri-state result:
    //   (name, null)  → assignable codebehind member; emit `this.<name> = __ctrl;`
    //   (null, type)  → member exists but isn't assignable from the resolved
    //                   control type. The framework would silently drop the
    //                   assignment, leaving the field null at runtime (NRE on
    //                   first access). Surfaced as TWF003 by the caller.
    //   (null, null)  → no such member; emitter declares one.
    private static (string? AssignableField, string? ConflictingType) ResolveCodeBehindField(
        Compilation compilation, INamedTypeSymbol? codeBehind, string id, string controlTypeMetadataName)
    {
        if (codeBehind is null || compilation.GetTypeByMetadataName(controlTypeMetadataName) is not INamedTypeSymbol controlType)
        {
            return (null, null);
        }

        string? firstConflict = null;
        for (INamedTypeSymbol? t = codeBehind; t != null; t = t.BaseType)
        {
            foreach (ISymbol member in t.GetMembers())
            {
                if (!string.Equals(member.Name, id, StringComparison.Ordinal)
                    || member.DeclaredAccessibility == Accessibility.Private)
                {
                    continue;
                }

                ITypeSymbol? target = member switch
                {
                    IFieldSymbol { IsConst: false, IsReadOnly: false } f => f.Type,
                    IPropertySymbol { SetMethod: not null } p => p.Type,
                    _ => null,
                };

                if (target is null)
                {
                    continue;
                }

                if (IsAssignableTo(controlType, target))
                {
                    return (member.Name, null);
                }

                // First settable same-named member whose type isn't assignable —
                // record so the caller can emit TWF003 if no assignable member
                // turns up higher in the hierarchy.
                firstConflict ??= target.ToDisplayString();
            }
        }

        return (null, firstConflict);
    }

    private static bool IsAssignableTo(ITypeSymbol from, ITypeSymbol to)
    {
        if (SymbolEqualityComparer.Default.Equals(from, to))
        {
            return true;
        }

        for (INamedTypeSymbol? b = from.BaseType; b != null; b = b.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(b, to))
            {
                return true;
            }
        }

        return from.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, to));
    }

    private static IPropertySymbol? FindSettableProperty(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? t = type; t != null; t = t.BaseType)
        {
            foreach (ISymbol member in t.GetMembers())
            {
                if (member is IPropertySymbol p
                    && !p.IsStatic
                    && p.DeclaredAccessibility == Accessibility.Public
                    && p.SetMethod is { DeclaredAccessibility: Accessibility.Public }
                    && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }
        }

        return null;
    }

    private static bool ImplementsAttributeAccessor(INamedTypeSymbol type)
    {
        return type.AllInterfaces.Any(i => i.ToDisplayString() == WebFormsContract.AttributeAccessor);
    }

    private static string? BuildValueExpression(ITypeSymbol propertyType, string value, Compilation compilation, ICollection<string>? layer3Fallbacks)
    {
        // Nullable<T>: convert the markup value through T's converter (via
        // Layer 1's invariant-culture pathway) and let C#'s implicit
        // T → T? lift it. Without this, every Nullable<T> property fell
        // through to the IAttributeAccessor.SetAttribute fallback — the
        // value parsed at render time as a string expando, the typed
        // property stayed null at runtime, and validators that gated on
        // the typed value rejected the page.
        if (propertyType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            return BuildValueExpression(nullable.TypeArguments[0], value, compilation, layer3Fallbacks);
        }

        if (propertyType.TypeKind == TypeKind.Enum)
        {
            // A flags enum attribute is a comma-separated list ("Move, Resize");
            // OR the members together. A single value is just the one-element case.
            string qualified = "global::" + propertyType.ToDisplayString();
            var members = new List<string>();
            foreach (string part in value.Split(','))
            {
                string token = part.Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                IFieldSymbol? field = propertyType.GetMembers().OfType<IFieldSymbol>()
                    .FirstOrDefault(f => f.IsConst && string.Equals(f.Name, token, StringComparison.OrdinalIgnoreCase));
                if (field is null)
                {
                    return null;
                }

                members.Add(qualified + "." + field.Name);
            }

            return members.Count == 0 ? null : string.Join(" | ", members);
        }

        // Layer 1: the type's own TypeConverter — when it's a type the analyzer
        // can reach as a runtime System.Type. ConvertTo(InstanceDescriptor) gives
        // us the exact construction recipe the converter author intended
        // (Color.FromArgb, TimeSpan.Parse, new Guid(...), …), so we don't have to
        // re-encode it on this side.
        if (TryConvertViaTypeConverter(propertyType, value) is { } converted)
        {
            return converted;
        }

        // Layer 2: framework-only types — see Emit/FrameworkOnlyValueExpressions.cs
        // for the full table (Unit, FontUnit, Color fallback, System.Type)
        // and how to extend it.
        if (FrameworkOnlyValueExpressions.TryEmit(propertyType, value, compilation) is { } framework)
        {
            return framework;
        }

        // Comma-separated array properties: Font-Names (string[]), PagerStyle
        // PageSizes (int[]), etc.
        if (propertyType is IArrayTypeSymbol array)
        {
            string[] items = value.Split(',');
            if (array.ElementType.SpecialType == SpecialType.System_String)
            {
                return "new string[] { " + string.Join(", ", items.Select(p => StringLiteral(p.Trim()))) + " }";
            }

            if (array.ElementType.SpecialType is SpecialType.System_Int32 && items.All(p => int.TryParse(p, out _)))
            {
                return "new int[] { " + string.Join(", ", items.Select(p => p.Trim())) + " }";
            }

            return null;
        }

        switch (propertyType.SpecialType)
        {
            case SpecialType.System_String:
                return StringLiteral(value);
            case SpecialType.System_Boolean:
                return bool.TryParse(value, out bool b) ? (b ? "true" : "false") : null;
            case SpecialType.System_Char:
                return value.Length == 1 ? "'" + (value == "'" ? "\\'" : value) + "'" : null;
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Int16:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_UInt16:
            case SpecialType.System_UInt32:
            case SpecialType.System_UInt64:
                return long.TryParse(value, out _) ? value.Trim() : null;
            case SpecialType.System_Double:
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? value.Trim() : null;
            case SpecialType.System_Single:
                return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? value.Trim() + "f" : null;
            case SpecialType.System_Decimal:
                return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? value.Trim() + "m" : null;
        }

        // Layer 3: residual Roslyn-side Parse(string) / ctor(string) discovery
        // for consumer-defined types whose assembly the analyzer can't load. Fires
        // TWF002 because a well-authored type would have surfaced this via its
        // TypeConverter (Layer 1).
        return TryEmitViaParseOrCtor(propertyType, value, layer3Fallbacks);
    }

    // Layer 1 bridge map: property-type metadata name → runtime System.Type
    // the analyzer can resolve at design time. Anything here is a candidate
    // for TypeDescriptor.GetConverter(t).ConvertTo(InstanceDescriptor);
    // anything outside falls through to Layer 2 (hardcoded framework-only
    // shapes) or Layer 3 (Roslyn-side Parse/ctor discovery, TWF002).
    //
    // To add an entry: pick a type whose assembly is reachable from a
    // modern Roslyn analyzer host (netstandard 2.0 + the BCL + whatever the
    // analyzer references). Framework-only types (anything in System.Web)
    // belong in Layer 2 instead — they'd return the default TypeConverter,
    // which can't produce an InstanceDescriptor, and Layer 1 would no-op.
    private static readonly Dictionary<string, Type> s_loadableTypes = new(StringComparer.Ordinal)
    {
        ["System.TimeSpan"] = typeof(TimeSpan),
        ["System.Guid"] = typeof(Guid),
        ["System.Uri"] = typeof(Uri),
        ["System.Version"] = typeof(Version),
        ["System.DateTime"] = typeof(DateTime),
        ["System.DateTimeOffset"] = typeof(DateTimeOffset),
        ["System.Net.IPAddress"] = typeof(System.Net.IPAddress),
        ["System.Drawing.Color"] = typeof(System.Drawing.Color),
    };

    private static string? TryConvertViaTypeConverter(ITypeSymbol propertyType, string value)
    {
        if (!s_loadableTypes.TryGetValue(propertyType.ToDisplayString(), out Type? runtimeType))
        {
            return null;
        }

        try
        {
            TypeConverter converter = TypeDescriptor.GetConverter(runtimeType!);
            // CanConvertTo(InstanceDescriptor) cleanly skips converters that
            // don't expose a construction recipe (default TypeConverter, and
            // some custom converters); without this probe their ConvertTo
            // call would throw and the catch would swallow it — same outcome
            // but noisier in a debugger.
            if (!converter.CanConvertFrom(typeof(string)) || !converter.CanConvertTo(typeof(InstanceDescriptor)))
            {
                return null;
            }

            object? instance = converter.ConvertFromInvariantString(value);
            if (instance is null)
            {
                return null;
            }

            return converter.ConvertTo(instance, typeof(InstanceDescriptor)) is InstanceDescriptor descriptor
                ? FormatInstanceDescriptor(descriptor)
                : null;
        }
        catch
        {
            // Value not parseable or descriptor failed to materialize — fall
            // through to the next layer. (The CanConvert probes above already
            // filter the common "doesn't support this" cases.)
            return null;
        }
    }

    private static string? FormatInstanceDescriptor(InstanceDescriptor descriptor)
    {
        Type? declaring = descriptor.MemberInfo?.DeclaringType;
        if (declaring is null)
        {
            return null;
        }

        string qualifier = "global::" + declaring.FullName;
        string args = FormatDescriptorArgs(descriptor.Arguments);

        return descriptor.MemberInfo switch
        {
            ConstructorInfo => $"new {qualifier}({args})",
            MethodInfo m => $"{qualifier}.{m.Name}({args})",
            PropertyInfo p => $"{qualifier}.{p.Name}",
            FieldInfo f => $"{qualifier}.{f.Name}",
            _ => null,
        };
    }

    private static string FormatDescriptorArgs(ICollection? args)
    {
        if (args is null || args.Count == 0)
        {
            return string.Empty;
        }

        var formatted = new List<string>(args.Count);
        foreach (object? arg in args)
        {
            string? piece = FormatArgValue(arg);
            if (piece is null)
            {
                // An argument we can't represent as a literal kills the whole
                // recipe — the caller will fall through to a later layer.
                throw new InvalidOperationException("unsupported InstanceDescriptor argument");
            }

            formatted.Add(piece);
        }

        return string.Join(", ", formatted);
    }

    private static string? FormatArgValue(object? arg) => arg switch
    {
        null => "null",
        string s => StringLiteral(s),
        bool b => b ? "true" : "false",
        char c => "'" + (c == '\'' ? "\\'" : c.ToString()) + "'",
        sbyte sb => sb.ToString(CultureInfo.InvariantCulture),
        byte by => by.ToString(CultureInfo.InvariantCulture),
        short sh => sh.ToString(CultureInfo.InvariantCulture),
        ushort us => us.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        uint ui => ui.ToString(CultureInfo.InvariantCulture) + "u",
        long l => l.ToString(CultureInfo.InvariantCulture) + "L",
        ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "uL",
        float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
        Enum e => "global::" + e.GetType().FullName + "." + e,
        _ => null,
    };

    private static string? TryEmitViaParseOrCtor(ITypeSymbol propertyType, string value, ICollection<string>? layer3Fallbacks)
    {
        string fullName = propertyType.ToDisplayString();

        // public static T Parse(string)
        foreach (ISymbol member in propertyType.GetMembers("Parse"))
        {
            if (member is IMethodSymbol parse
                && parse.IsStatic
                && parse.DeclaredAccessibility == Accessibility.Public
                && parse.Parameters.Length == 1
                && parse.Parameters[0].Type.SpecialType == SpecialType.System_String
                && SymbolEqualityComparer.Default.Equals(parse.ReturnType, propertyType))
            {
                layer3Fallbacks?.Add(fullName);
                return $"global::{fullName}.Parse({StringLiteral(value)})";
            }
        }

        // public T(string) ctor
        foreach (IMethodSymbol ctor in propertyType.GetMembers().OfType<IMethodSymbol>())
        {
            if (ctor.MethodKind == MethodKind.Constructor
                && ctor.DeclaredAccessibility == Accessibility.Public
                && ctor.Parameters.Length == 1
                && ctor.Parameters[0].Type.SpecialType == SpecialType.System_String)
            {
                layer3Fallbacks?.Add(fullName);
                return $"new global::{fullName}({StringLiteral(value)})";
            }
        }

        return null;
    }

    private static readonly DiagnosticDescriptor s_layer3Fallback = new(
        "TWF002",
        "WebForms property bound via Parse/ctor fallback",
        "Property type '{0}' was emitted via Parse(string) / ctor(string) discovery — no TypeConverter produced an InstanceDescriptor. Attach a [TypeConverter] that supports InstanceDescriptor for richer codegen.",
        "Triaxis.WebForms",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // A markup control resolves to a type that isn't assignable to the
    // same-named existing codebehind field. The framework would silently
    // drop the field assignment, leaving the field null at runtime (NRE
    // on first access). Surfacing as Error means the build fails with a
    // fixable diagnostic instead of going green and crashing in the
    // browser.
    private static readonly DiagnosticDescriptor s_fieldBindingMismatch = new(
        "TWF003",
        "WebForms control type not assignable to codebehind field",
        "Control '{0}' (in {3}) resolves to '{1}', which is not assignable to the existing codebehind field of type '{2}'. The field would silently stay null at runtime — either change the codebehind field type to one assignable from '{1}', rename one side, or drop the runat=\"server\" on the markup.",
        "Triaxis.WebForms",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // Forwarder kept for the file's existing call sites (no behavior change).
    // New code should call Emit.CodeLiteral.Escape directly.
    internal static string StringLiteral(string value) => CodeLiteral.Escape(value);

    private const string ParserAccessorMetadata = "System.Web.UI.IParserAccessor";

    // Routes a child element under a parent control. The cardinal rule: never
    // emit an IParserAccessor cast for a type that doesn't implement it.
    private static ChildPlacement ClassifyChild(Compilation compilation, string parentMetadataName, string childLocalName, bool childIsServerControl)
    {
        if (compilation.GetTypeByMetadataName(parentMetadataName) is not INamedTypeSymbol parent)
        {
            return ChildPlacement.ParsedSubObject();
        }

        (bool childrenAsProperties, string? defaultProperty) = GetParseChildren(parent);

        // A control with ParseChildren(false) is a control container; everything
        // else — a ParseChildren(true) control or a non-control complex object
        // (CompositeScriptReference, GridTableView) — treats children as
        // property elements.
        if (!childrenAsProperties && ImplementsInterface(parent, ParserAccessorMetadata))
        {
            return ChildPlacement.ParsedSubObject();
        }

        IPropertySymbol? property = FindGettableProperty(parent, childLocalName);
        if (property != null)
        {
            if (IsTemplateType(property.Type))
            {
                return ChildPlacement.Template(property.Name, ReadTemplateContainer(property), IsSingleInstanceTemplate(property), IsTwoWayTemplate(property));
            }

            if (IsCollectionType(property.Type))
            {
                return ChildPlacement.CollectionElement(property.Name);
            }

            if (property.Type.TypeKind == TypeKind.Class && property.Type.SpecialType != SpecialType.System_String)
            {
                return ChildPlacement.ObjectElement(property.Name, MetadataNameOf(property.Type));
            }

            return ChildPlacement.Skip($"scalar property element '{childLocalName}'");
        }

        if (childIsServerControl && defaultProperty != null
            && FindGettableProperty(parent, defaultProperty) is { } dp && IsCollectionType(dp.Type))
        {
            return ChildPlacement.CollectionItem(dp.Name);
        }

        return ChildPlacement.Skip($"no property '{childLocalName}' on {parentMetadataName}");
    }

    private static bool IsControlContainer(Compilation compilation, string parentMetadataName)
    {
        if (compilation.GetTypeByMetadataName(parentMetadataName) is not INamedTypeSymbol parent)
        {
            return true;
        }

        return !GetParseChildren(parent).ChildrenAsProperties && ImplementsInterface(parent, ParserAccessorMetadata);
    }

    // Resolves a child tag's type + whether to fold its children as property
    // elements. A server control resolves through the tag map; a property element
    // (no-prefix wrapper like <Scripts>/<Columns>) resolves to the parent's
    // property type. Property containers: a ParseChildren(true) control, any
    // non-control complex object, or a non-template property — but NOT a template
    // (whose content is controls/literals).
    private static (string? TypeMetadata, bool IsPropertyContainer) ResolveChild(
        Compilation compilation, ControlTypeResolver resolver, string? parentMetadata, string rawName, bool isServer)
    {
        if (isServer)
        {
            int colon = rawName.IndexOf(':');
            string? prefix = colon < 0 ? null : rawName.Substring(0, colon);
            string tag = colon < 0 ? rawName : rawName.Substring(colon + 1);
            if (resolver.Resolve(prefix, tag, parentMetadata: parentMetadata) is not { } resolved)
            {
                return (null, false);
            }

            INamedTypeSymbol? sym = compilation.GetTypeByMetadataName(resolved.MetadataName);
            return (resolved.MetadataName, sym != null && IsPropertyContainerType(sym));
        }

        if (parentMetadata == null || compilation.GetTypeByMetadataName(parentMetadata) is not INamedTypeSymbol parent
            || FindGettableProperty(parent, rawName) is not { } property)
        {
            return (null, false);
        }

        string propMeta = MetadataNameOf(property.Type);
        return (propMeta, !IsTemplateType(property.Type));
    }

    private static bool IsPropertyContainerType(INamedTypeSymbol type)
    {
        return GetParseChildren(type).ChildrenAsProperties || !ImplementsInterface(type, ParserAccessorMetadata);
    }

    private static (bool ChildrenAsProperties, string? DefaultProperty) GetParseChildren(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t != null; t = t.BaseType)
        {
            foreach (AttributeData attr in t.GetAttributes())
            {
                if (attr.AttributeClass?.Name != "ParseChildrenAttribute")
                {
                    continue;
                }

                bool asProperties = false;
                string? defaultProperty = null;
                foreach (TypedConstant arg in attr.ConstructorArguments)
                {
                    if (arg.Value is bool b) { asProperties = b; }
                    else if (arg.Value is string s) { asProperties = true; defaultProperty = s; }
                }

                foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
                {
                    if (named.Key == "ChildrenAsProperties" && named.Value.Value is bool nb) { asProperties = nb; }
                    else if (named.Key == "DefaultProperty" && named.Value.Value is string ns) { defaultProperty = ns; }
                }

                return (asProperties, defaultProperty);
            }
        }

        return (false, null);
    }

    private static IPropertySymbol? FindGettableProperty(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? t = type; t != null; t = t.BaseType)
        {
            foreach (ISymbol member in t.GetMembers())
            {
                if (member is IPropertySymbol p
                    && !p.IsStatic
                    && p.GetMethod is { DeclaredAccessibility: Accessibility.Public }
                    && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }
        }

        return null;
    }

    private static bool IsCollectionType(ITypeSymbol type)
    {
        for (INamedTypeSymbol? t = type as INamedTypeSymbol; t != null; t = t.BaseType)
        {
            foreach (ISymbol member in t.GetMembers("Add"))
            {
                if (member is IMethodSymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public } m
                    && m.Parameters.Length == 1)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? ReadTemplateContainer(IPropertySymbol property)
    {
        foreach (AttributeData attr in property.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "TemplateContainerAttribute"
                && attr.ConstructorArguments.Length >= 1
                && attr.ConstructorArguments[0].Value is INamedTypeSymbol container)
            {
                return MetadataNameOf(container);
            }
        }

        return null;
    }

    // [TemplateInstance(TemplateInstance.Single)] (e.g. UpdatePanel.ContentTemplate)
    // means the template is instantiated once in the page's naming container, so
    // its controls become page fields — unlike the default Multiple (per-item)
    // templates such as a grid's ItemTemplate.
    private static bool IsSingleInstanceTemplate(IPropertySymbol property)
    {
        foreach (AttributeData attr in property.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "TemplateInstanceAttribute"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is int instance)
            {
                return instance == 1; // TemplateInstance.Single
            }
        }

        return false;
    }

    // [TemplateContainer(typeof(X), BindingDirection.TwoWay)] makes the template
    // property an IBindableTemplate, which the ASP.NET compiler fills with a
    // CompiledBindableTemplateBuilder.
    private static bool IsTwoWayTemplate(IPropertySymbol property)
    {
        foreach (AttributeData attr in property.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "TemplateContainerAttribute"
                && attr.ConstructorArguments.Length >= 2
                && attr.ConstructorArguments[1].Value is int direction)
            {
                return direction == 1; // BindingDirection.TwoWay
            }
        }

        return false;
    }

    private static bool IsTemplateType(ITypeSymbol type)
    {
        return type.ToDisplayString() == WebFormsContract.Template
            || type.AllInterfaces.Any(i => i.ToDisplayString() == WebFormsContract.Template);
    }

    private static bool ImplementsInterface(INamedTypeSymbol type, string interfaceMetadataName)
    {
        return type.AllInterfaces.Any(i => i.ToDisplayString() == interfaceMetadataName);
    }

    private static string MetadataNameOf(ITypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
    }

    // Returns the canonical, correctly-cased metadata name of a constructible
    // control type, or null. Markup tag names are case-insensitive, so an exact
    // miss falls back to a case-insensitive search of the namespace's types.
    private static string? CanonicalConstructibleType(Compilation compilation, string metadataName)
    {
        if (compilation.GetTypeByMetadataName(metadataName) is INamedTypeSymbol exact)
        {
            return IsConstructible(exact) ? metadataName : null;
        }

        int dot = metadataName.LastIndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        INamespaceSymbol? ns = ResolveNamespace(compilation, metadataName.Substring(0, dot));
        if (ns is null)
        {
            return null;
        }

        string name = metadataName.Substring(dot + 1);
        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
        {
            if (string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase) && IsConstructible(type))
            {
                return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
            }
        }

        return null;
    }

    private static bool IsConstructible(INamedTypeSymbol symbol)
    {
        return symbol.TypeKind == TypeKind.Class
            && !symbol.IsAbstract
            && !symbol.IsStatic
            && symbol.InstanceConstructors.Any(c =>
                c.Parameters.Length == 0
                && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal);
    }

    private static INamespaceSymbol? ResolveNamespace(Compilation compilation, string ns)
    {
        INamespaceSymbol? current = compilation.GlobalNamespace;
        foreach (string part in ns.Split('.'))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static string? ParseInherits(string markup)
    {
        if (string.IsNullOrEmpty(markup))
        {
            return null;
        }

        Match m = s_inheritsAttribute.Match(markup);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static ImmutableDictionary<string, string> MergeCodeBehind(ImmutableArray<(string Type, string? Inherits)> entries)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string type, string? inherits) in entries)
        {
            if (!string.IsNullOrEmpty(inherits))
            {
                builder[type] = inherits!;
            }
        }

        return builder.ToImmutable();
    }

    private static bool IsControlType(Compilation compilation, INamedTypeSymbol? controlSymbol, string metadataName)
    {
        return controlSymbol != null
            && compilation.GetTypeByMetadataName(metadataName) is INamedTypeSymbol type
            && IsAssignableTo(type, controlSymbol);
    }

    // Mirrors ThemeableAttribute.IsTypeThemeable: a Control whose nearest
    // [Themeable] (walking base types) is true, defaulting true when absent.
    // The ASP.NET compiler emits ApplyStyleSheetSkin for exactly these types,
    // skipping HtmlControls, ScriptManager, and non-Control sub-objects (all
    // marked [Themeable(false)] or simply not Controls).
    private static bool IsThemeable(Compilation compilation, INamedTypeSymbol? controlSymbol, string metadataName)
    {
        if (controlSymbol is null
            || compilation.GetTypeByMetadataName(metadataName) is not INamedTypeSymbol type
            || !IsAssignableTo(type, controlSymbol))
        {
            return false;
        }

        for (INamedTypeSymbol? t = type; t != null; t = t.BaseType)
        {
            foreach (AttributeData attribute in t.GetAttributes())
            {
                if (attribute.AttributeClass?.Name == "ThemeableAttribute"
                    && attribute.ConstructorArguments.Length == 1
                    && attribute.ConstructorArguments[0].Value is bool themeable)
                {
                    return themeable;
                }
            }
        }

        return true;
    }

    private static bool NamespaceExists(Compilation compilation, string ns)
    {
        INamespaceSymbol? current = compilation.GlobalNamespace;
        foreach (string part in ns.Split('.'))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current is null)
            {
                return false;
            }
        }

        return true;
    }

    private readonly struct MarkupFile : IEquatable<MarkupFile>
    {
        public MarkupFile(string virtualPath, string content)
        {
            VirtualPath = virtualPath;
            Content = content;
        }

        public string VirtualPath { get; }
        public string Content { get; }

        public bool Equals(MarkupFile other)
        {
            return VirtualPath == other.VirtualPath && Content == other.Content;
        }

        public override bool Equals(object? obj) => obj is MarkupFile other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = VirtualPath.GetHashCode();
                hash = (hash * 397) ^ Content.GetHashCode();
                return hash;
            }
        }
    }
}
