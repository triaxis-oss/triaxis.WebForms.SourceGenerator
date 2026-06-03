# triaxis.WebForms.SourceGenerator

A Roslyn incremental source generator that compiles ASP.NET WebForms markup
(`.aspx`, `.ascx`, `.master`, `.asax`) into the host assembly at build time,
replacing the Windows-only `aspnet_compiler.exe` + `aspnet_merge.exe`
precompile step.

The generator emits, per markup file, an `ASP.<name>_aspx : <Inherits>` page
type (frame + `__BuildControl*` control tree + `FrameworkInitialize`) and a
matching lean `.compiled` stub in the output directory. Together they let
`System.Web`'s `BuildManager` serve the page from the main app assembly with
no runtime markup compilation and **no `aspnet_compiler` involvement**, so
WebForms apps can be built on Linux as part of an ordinary `dotnet build`.

## Status

Pre-release. The generator has been validated against a 303-file corpus of
real-world markup (Telerik / AjaxControlToolkit / user controls, nested
masters), but the public surface is still settling — expect breaking
changes until 1.0.

## Install

```shell
dotnet add package triaxis.WebForms.SourceGenerator
```

The package ships only an analyzer DLL (`analyzers/dotnet/cs/`) and a
`buildTransitive` targets file. There is no runtime library — the generated
page types are compiled directly into your assembly.

## What the package does to your project

When referenced, the bundled `buildTransitive` targets:

- Glob `**/*.aspx`, `**/*.ascx`, `**/*.master`, `Global.asax`, and `web.config`
  into `AdditionalFiles` (opt out with `<TriaxisWebFormsCollectMarkup>false</TriaxisWebFormsCollectMarkup>`).
- Verify before `Build` that `PrecompiledApp.config` exists at the project
  root next to `web.config`. Without it `BuildManager` never enters
  precompiled mode and the generated pages are ignored at runtime — so the
  check fails the build (error `TWF001`) with a copy-pasteable fix. Opt
  out with `<TriaxisWebFormsCheckPrecompiledAppConfig>false</TriaxisWebFormsCheckPrecompiledAppConfig>`.
- After `Build`, emit a lean `.compiled` stub per markup file into
  `$(OutDir)` pointing `BuildManager` at the Roslyn-generated
  `ASP.<name>_aspx` type.

## How markup attribute values are converted

The generator emits each markup attribute value through three layers:

1. **The property type's own `TypeConverter`.** For types the analyzer can
   reach as a runtime `System.Type` (`TimeSpan`, `Guid`, `Uri`, `Version`,
   `DateTime`, `DateTimeOffset`, `IPAddress`, `System.Drawing.Color`, …)
   the converter's `ConvertTo(InstanceDescriptor)` recipe is emitted
   verbatim — `Color.Red` for a named color, `Color.FromArgb(…)` for a
   hex triplet, `TimeSpan.Parse("01:02:03")`, `new Guid("…")`. Any
   property typed against a third-party type that reuses one of these
   converters picks up the same codegen for free.
2. **Hardcoded patterns for Framework-only types.** `Unit`, `FontUnit`,
   `WebColor` and the rest of `System.Web.UI.WebControls`' typed-value
   surface live in `System.Web.dll`, which is .NET-Framework-only. The
   analyzer process runs on modern .NET (often on Linux) and cannot load
   `System.Web` to instantiate `UnitConverter` / `FontUnitConverter` /
   `WebColorConverter` at design time, so Layer 1 is unreachable for
   these types and the generator hardcodes the construction shape that
   matches the `aspnet_compiler` oracle. Extending support for a new
   System.Web property type means adding a case here — the natural
   design-time pathway is closed off by the framework boundary.
3. **Roslyn-side `Parse(string)` / `ctor(string)` discovery.** Residual
   catch-all for consumer-defined types whose assemblies the analyzer
   can't load. Fires diagnostic **TWF002** so a well-authored type can
   attach a `[TypeConverter]` and graduate to Layer 1.

## License

[MIT](./LICENSE.txt). Copyright &copy; 2026 triaxis s.r.o.

The vendored Mono `AspParser` front-end under
`src/triaxis.WebForms.SourceGenerator/Vendor/Mono/` carries its own
MIT attribution; see the per-file headers.
