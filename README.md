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

Production-ready. The generator has been validated against a 303-file
corpus of real-world markup (Telerik / AjaxControlToolkit / user controls,
nested masters, two-way data binding, theming, master/content pages) and
ships under semantic versioning from 1.0.

## Not a Microsoft product

This package is **not** affiliated with, endorsed by, or supported by
Microsoft. ASP.NET WebForms remains a Microsoft technology and
`System.Web` itself is shipped and supported by Microsoft as part of
the .NET Framework, but the build-time alternative to `aspnet_compiler`
implemented here is an independent third-party project. The reverse-
engineered shapes the generator emits (page frame, `__BuildControl*`
methods, `.compiled` sidecar format, `PrecompiledApp.config` semantics)
are derived from observing what `aspnet_compiler` and `BuildManager`
do — not from Microsoft documentation that guarantees they will keep
doing it. Use at your own risk; report issues here, not to Microsoft.

## Install

```shell
dotnet add package triaxis.WebForms.SourceGenerator
```

The package ships the analyzer DLL (`analyzers/dotnet/cs/`), an MSBuild
task assembly (`tasks/netstandard2.0/`), and `buildTransitive` `.props` /
`.targets`. There is no runtime library — the generated page types are
compiled directly into your assembly.

A typical WebForms app csproj boils down to:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="triaxis.WebForms.SourceGenerator" />
  </ItemGroup>
</Project>
```

The package fills in the rest: output layout, content collection, page
compilation, publish layout, web.config transforms.

## What the package does at build time

When referenced, the bundled `buildTransitive` `.props` / `.targets`:

- Default `OutputPath` to `bin\` and disable the
  `bin\<Configuration>\<TargetFramework>\` appends, so build output
  lands in the same `bin\` directory IIS probes. A consumer that
  explicitly sets `OutputPath` / `AppendTargetFrameworkToOutputPath`
  wins.
- Glob the WebForms content corpus into `Content` so the SDK publish
  pipeline ships it. `WebFormsGenContentInclude` holds the default
  globs — markup, `web.config` (root + per-folder overrides),
  `PrecompiledApp.config`, the `App_Themes\**` tree, the handlers
  (`*.ashx` / `*.asmx` / `*.svc`), the standard script / style /
  image / font extensions. App-specific configs (`NLog.config`,
  `log4net.config`, …) are deliberately not in the default — pull
  them in via `<Content Include="..." />`. `WebFormsGenContentExclude`
  carries the standard build-folder excludes (`bin\**` / `obj\**` /
  `node_modules\**` / `**\*.intellisense.js`).

  The package's item additions live in its `.props` (loaded BEFORE
  the consumer csproj body), so standard MSBuild `Content Remove` /
  `Include` / `Update` in the consumer body work against them:
  ```xml
  <ItemGroup>
    <Content Remove="App_Data\**;Logs\**;Uploads\**" />
    <Content Include="NLog.config;Docs\**\*.pdf" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
  ```
  Or extend the package's property defaults:
  ```xml
  <PropertyGroup>
    <WebFormsGenContentInclude>$(WebFormsGenContentInclude);**\*.pdf;**\*.docx</WebFormsGenContentInclude>
    <WebFormsGenContentExclude>$(WebFormsGenContentExclude);Logs\**</WebFormsGenContentExclude>
  </PropertyGroup>
  ```
- Glob the markup corpus (`*.aspx` / `*.ascx` / `*.master` /
  `Global.asax`) and `web.config` into `AdditionalFiles` for the
  generator.

All default item contributions above (Content + AdditionalFiles) ride
the single `EnableDefaultWebFormsGenItems` switch, which mirrors the
SDK's `EnableDefaultItems` / `EnableDefaultContentItems` pattern: it
cascades from `$(EnableDefaultItems)` (so flipping the SDK switch
disables the package's defaults too), and the consumer can opt out
just the package's contribution with
`<EnableDefaultWebFormsGenItems>false</EnableDefaultWebFormsGenItems>`
to take over `Content` and `AdditionalFiles` itself.
- Verify before `Build` that `PrecompiledApp.config` exists at the project
  root next to `web.config`. Without it `BuildManager` never enters
  precompiled mode and the generated pages are ignored at runtime — so the
  check fails the build (error `TWF001`) with a copy-pasteable fix. Opt
  out with `<WebFormsGenCheckPrecompiledAppConfig>false</WebFormsGenCheckPrecompiledAppConfig>`.
- After `Build`, emit a lean `.compiled` stub per markup file into
  `$(OutDir)` pointing `BuildManager` at the Roslyn-generated
  `ASP.<name>_aspx` type.
- Emit a `.compiled` stub per handler (`.asmx` / `.ashx` / `.svc`) too.
  These aren't source-generated — the directive names a prebuilt type —
  but a non-updatable precompiled app still rejects any virtual path
  without a sidecar ("has not been pre-compiled, and cannot be
  requested"). `.asmx` / `.ashx` bind straight to the directive's
  `Class` (`resultType=2`), resolved to the assembly that defines it by
  reading the build outputs; a WCF `.svc` emits the `Service` directive's
  `resultType=5` custom string carrying the deployed assembly closure the
  host resolves the service type against.

## What the package does at publish time

`dotnet publish` produces a complete, deployable ASP.NET WebForms layout
out of the box. Each step is gated by its own property, so consumers
that already maintain an equivalent step can opt out cleanly:

- **Add the `.compiled` sidecars to the publish file list.** They're
  emitted to `$(OutDir)` by the build target above but aren't members of
  any built-in publish item group, so the SDK skips them without this
  hook. Opt out: `<WebFormsGenPublishCompiled>false</WebFormsGenPublishCompiled>`.
- **Relocate every binary to `<publish>/bin/`.** The host assembly, its
  `.pdb` / `.deps.json`, and the `.compiled` sidecars move to `bin/`;
  everything that came in via the SDK's content pipeline (`.aspx` /
  `.ascx` / `.master`, `web.config`, `PrecompiledApp.config`, static
  assets) stays at the publish root. Opt out:
  `<WebFormsGenBinariesInBin>false</WebFormsGenBinariesInBin>`.

  For native helpers and other files that arrive as content but need
  to sit alongside the host assembly (e.g. `WinSCP.exe`), list them in
  `WebFormsGenAdditionalBinaries` (semicolon-separated, matched by
  exact filename + extension):
  ```xml
  <PropertyGroup>
    <WebFormsGenAdditionalBinaries>WinSCP.exe;NativeHelper.dll</WebFormsGenAdditionalBinaries>
  </PropertyGroup>
  ```
- **Zero out the published markup and handler files.** Each `.aspx` /
  `.ascx` / `.master` and each `.asmx` / `.ashx` / `.svc` in
  `$(PublishDir)` is truncated to zero bytes. The page/handler type
  already lives in (or is referenced by) the app and the `.compiled`
  sidecar routes `BuildManager` / the WCF host to it, so the file only
  needs to exist for the runtime probe — its content (and the internal
  type names a handler directive would otherwise expose) can leave the
  deployment. Opt out:
  `<WebFormsGenStripPublishedMarkup>false</WebFormsGenStripPublishedMarkup>`.
- **XDT-transform `web.config`.** If `web.$(Configuration).config`
  (e.g. `web.Release.config`, `web.Debug.config`) exists at the project
  root, it's applied to the published `web.config` using the
  `TransformXml` MSBuild task shipped by `Microsoft.NET.Sdk.Publish`.
  Source `web.config` is untouched. Opt out:
  `<WebFormsGenTransformConfig>false</WebFormsGenTransformConfig>`.

A typical `dotnet publish` of a WebForms app produces:

```
<publish>/
  bin/
    <App>.dll
    <App>.pdb
    <App>.deps.json
    App_global.asax.compiled
    <page>.<hash>.compiled  …  (one per markup file)
  web.config                  (transformed)
  PrecompiledApp.config
  Forms/Foo.aspx              (zero bytes)
  …
```

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
