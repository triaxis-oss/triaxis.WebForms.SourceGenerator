; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TWF002  | Triaxis.WebForms | Warning | Property type bound via Parse(string) / ctor(string) fallback — consider attaching a [TypeConverter] that supplies an InstanceDescriptor.

## Release 1.0.1

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TWF003  | Triaxis.WebForms | Error | Resolved control type isn't assignable to the same-named codebehind field — the framework would silently drop the assignment, leaving the field null at runtime.

## Release 1.0.10

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TWF004  | Triaxis.WebForms | Warning | Directive attribute the generator doesn't act on — the page asks for behavior it silently won't get.
