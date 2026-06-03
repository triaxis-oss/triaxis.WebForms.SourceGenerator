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

## License

[MIT](./LICENSE.txt). Copyright &copy; 2026 triaxis s.r.o.
